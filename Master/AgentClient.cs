using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using RainDrop.Shared;

namespace RainDrop.Master;

/// <summary>
/// การเชื่อมต่อไปยัง Agent หนึ่งเครื่อง
/// - การเขียนทั้งหมดผ่านคิว (Channel) ตัวเดียว เพื่อให้ลำดับคำสั่ง เมาส์/คีย์บอร์ด ไม่สลับกัน
/// - มีตัวอ่าน (reader loop) ทำงานตลอดเวลา แยกข้อมูลที่เข้ามาเป็น
///     ScreenFrame  -> ส่งให้คำขอภาพที่รออยู่
///     ChatMessage  -> ยิง event ChatReceived (Agent ส่งมาเองได้ทุกเมื่อ)
///     VoiceMessage -> ยิง event VoiceReceived
/// </summary>
internal sealed class AgentClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly SslStream _stream;
    private readonly MasterPki _pki;
    private readonly string _remote;
    private readonly Channel<(MsgType Type, byte[] Payload)> _outbox =
        Channel.CreateUnbounded<(MsgType, byte[])>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _screenLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private volatile TaskCompletionSource<byte[]>? _pendingFrame;

    public bool IsConnected { get; private set; }

    /// <summary>ถูกเรียกบน thread pool ผู้ใช้ต้องสลับกลับไปยัง UI thread เอง</summary>
    public event Action<ChatMsg>? ChatReceived;

    /// <summary>ถูกเรียกบน thread pool เหมือน ChatReceived</summary>
    public event Action<VoiceMsg>? VoiceReceived;

    /// <summary>แจ้งเตือนจากเครื่องลูก (USB/ไฟล์/บล็อกโปรแกรม) ถูกเรียกบน thread pool</summary>
    public event Action<AlertMsg>? AlertReceived;

    private AgentClient(TcpClient tcp, SslStream stream, MasterPki pki, string remote)
    {
        _tcp = tcp;
        _stream = stream;
        _pki = pki;
        _remote = remote;
        IsConnected = true;
        _ = Task.Run(WriterLoopAsync);
        _ = Task.Run(ReaderLoopAsync);
    }

    public static async Task<AgentClient> ConnectAsync(string host, int port, MasterKey key, MasterPki pki, string machineName, CancellationToken ct = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            string remote = (tcp.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? host;
            string? expected = pki.ExpectedSerial(machineName);
            if (expected is null) throw new UnauthorizedAccessException("เครื่องนี้ยังไม่มี enrollment bundle ใน Master");
            var policy = new ControlTlsPolicy(pki.CaCertificate, new RevocationState(pki.RevocationSnapshot()), ControlRole.AgentServer);
            SslStream stream;
            try
            {
                stream = await ControlTls.AuthenticateClientAsync(tcp.GetStream(), host, pki.ClientCertificate, policy, cts.Token).ConfigureAwait(false);
            }
            catch (ControlTlsException ex)
            {
                pki.Audit.Append(new AuditEvent("tls-fail", remote, "denied", ex.Reason));
                throw new IOException("TLS ปฏิเสธการเชื่อมต่อ: " + ex.Reason);
            }
            using var remoteCert = new X509Certificate2(stream.RemoteCertificate!);
            if (!string.Equals(RepoCertificates.Serial(remoteCert), expected, StringComparison.OrdinalIgnoreCase))
            {
                stream.Dispose();
                pki.Audit.Append(new AuditEvent("auth-fail", remote, "denied", "agent-identity-mismatch"));
                throw new UnauthorizedAccessException("ใบรับรองไม่ตรงกับเครื่องที่ลงทะเบียนไว้");
            }

            var (type, nonce) = await Protocol.ReadAsync(stream, cts.Token).ConfigureAwait(false);
            if (type != MsgType.AuthChallenge || nonce.Length != 32)
                throw new InvalidDataException("ไม่ใช่ Rain Drop Agent");

            await Protocol.WriteAsync(stream, MsgType.AuthResponse, key.Sign(nonce), cts.Token).ConfigureAwait(false);

            var (resultType, result) = await Protocol.ReadAsync(stream, cts.Token).ConfigureAwait(false);
            if (resultType != MsgType.AuthResult || result.Length != 1 || result[0] != 1)
            {
                pki.Audit.Append(new AuditEvent("auth-fail", remote, "denied", "challenge"));
                throw new UnauthorizedAccessException(
                    $"กุญแจไม่ตรงกัน: ติดตั้งเครื่องลูกใหม่ด้วย enrollment bundle ของ Master นี้ (ลายนิ้วมือ Master: {key.Fingerprint})");
            }

            await Protocol.WriteAsync(stream, ControlMessages.RevocationList, Protocol.ToJson(pki.RevocationSnapshot()), cts.Token).ConfigureAwait(false);
            var (ackType, _) = await Protocol.ReadAsync(stream, cts.Token).ConfigureAwait(false);
            if (ackType != ControlMessages.RevocationAck) throw new InvalidDataException("Agent did not acknowledge revocation snapshot");
            pki.Audit.Append(new AuditEvent("connect", remote, "ok", expected));
            return new AgentClient(tcp, stream, pki, remote);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            tcp.Dispose();
            throw new TimeoutException("หมดเวลา: IP ผิด, เครื่องปิดอยู่ หรือไฟร์วอลล์ของเครื่องลูกบล็อกพอร์ต " + port);
        }
        catch (SocketException ex)
        {
            tcp.Dispose();
            throw new IOException(ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "เครื่องลูกปฏิเสธการเชื่อมต่อ: Agent ไม่ได้ทำงาน (ไม่มีไอคอนโล่)",
                SocketError.HostNotFound or SocketError.NoData => "ไม่พบชื่อเครื่องนี้ในเครือข่าย ลองใส่เป็น IP แทน",
                SocketError.HostUnreachable or SocketError.NetworkUnreachable => "ติดต่อเครื่องนี้ไม่ได้: อยู่คนละวงเครือข่าย",
                SocketError.TimedOut => "หมดเวลา: IP ผิด, เครื่องปิดอยู่ หรือไฟร์วอลล์บล็อก",
                _ => ex.Message,
            });
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private async Task WriterLoopAsync()
    {
        try
        {
            await foreach (var (type, payload) in _outbox.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                await Protocol.WriteAsync(_stream, type, payload, _cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // เขียนไม่สำเร็จ = การเชื่อมต่อขาด
        }
        finally
        {
            MarkDisconnected();
        }
    }

    private async Task ReaderLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (type, payload) = await Protocol.ReadAsync(_stream, _cts.Token).ConfigureAwait(false);
                switch (type)
                {
                    case MsgType.ScreenFrame:
                        _pendingFrame?.TrySetResult(payload);
                        break;

                    case MsgType.ChatMessage:
                        try { ChatReceived?.Invoke(Protocol.FromJson<ChatMsg>(payload)); }
                        catch (Exception ex)
                        {
                            // ข้อความผิดรูปแบบจากเครือข่าย หรือ handler ฝั่ง UI โยน: ข้ามข้อความนี้
                            // เพื่อให้ลูปอ่านยังรับภาพหน้าจอ/คำสั่งอื่นต่อได้ แต่ต้องไม่เห็นเงียบ ๆ
                            Diag.Warn("AgentClient.ReaderLoop", "จัดการข้อความแชทไม่สำเร็จ ข้ามข้อความนี้", ex);
                        }
                        break;

                    case MsgType.VoiceMessage:
                        try { VoiceReceived?.Invoke(Protocol.FromJson<VoiceMsg>(payload)); }
                        catch (Exception ex)
                        {
                            // เหตุผลเดียวกับแชท: ข้ามเฉพาะข้อความเสียงที่เสีย ลูปอ่านต้องทำงานต่อ
                            Diag.Warn("AgentClient.ReaderLoop", "จัดการข้อความเสียงไม่สำเร็จ ข้ามข้อความนี้", ex);
                        }
                        break;

                    case MsgType.ActivitySnapshot:
                        _pendingActivity?.TrySetResult(payload);
                        break;

                    case MsgType.Alert:
                        try { AlertReceived?.Invoke(Protocol.FromJson<AlertMsg>(payload)); }
                        catch (Exception ex)
                        {
                            // เหตุผลเดียวกับแชท: การแจ้งเตือนที่เสียต้องไม่ตัดการเชื่อมต่อทั้งหมด
                            Diag.Warn("AgentClient.ReaderLoop", "จัดการการแจ้งเตือนไม่สำเร็จ ข้ามข้อความนี้", ex);
                        }
                        break;
                }
            }
        }
        catch
        {
            // อ่านไม่สำเร็จ = การเชื่อมต่อขาด
        }
        finally
        {
            MarkDisconnected();
        }
    }

    private void MarkDisconnected()
    {
        IsConnected = false;
        _pendingFrame?.TrySetException(new IOException("ขาดการเชื่อมต่อ"));
        _pendingActivity?.TrySetException(new IOException("ขาดการเชื่อมต่อ"));
    }

    private readonly SemaphoreSlim _activityLock = new(1, 1);
    private volatile TaskCompletionSource<byte[]>? _pendingActivity;

    /// <summary>ขอสรุปกิจกรรมล่าสุดจากเครื่องลูก</summary>
    public async Task<ActivitySnapshot> RequestActivityAsync(CancellationToken ct = default)
    {
        await _activityLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingActivity = tcs;
            if (!IsConnected) throw new IOException("ขาดการเชื่อมต่อ");

            Send(MsgType.ActivityRequest);
            byte[] json = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            return Protocol.FromJson<ActivitySnapshot>(json);
        }
        finally
        {
            _pendingActivity = null;
            _activityLock.Release();
        }
    }

    public void Send(MsgType type, byte[]? payload = null)
    {
        if (!IsConnected) return;
        string? eventName = type switch
        {
            MsgType.Lock => "lock", MsgType.Unlock => "unlock", MsgType.PowerAction => "power",
            MsgType.RunCommand => "run", MsgType.SetBlocklist => "blocklist", _ => null,
        };
        if (eventName is not null)
        {
            try { _pki.Audit.Append(new AuditEvent(eventName, _remote, "sent")); }
            catch (IOException ex)
            {
                Diag.Error("AgentClient.Send", "audit unavailable; command not sent", ex);
                Dispose();
                return;
            }
        }
        _outbox.Writer.TryWrite((type, payload ?? Array.Empty<byte>()));
    }

    public void SendJson<T>(MsgType type, T obj) => Send(type, Protocol.ToJson(obj));

    /// <summary>ส่งรายการบล็อกโปรแกรม/เว็บไปยังเครื่องลูก</summary>
    public void SendBlocklist(BlockRules rules) => SendJson(MsgType.SetBlocklist, rules);

    public async Task<Bitmap> RequestScreenAsync(int maxWidth, int quality, CancellationToken ct = default)
    {
        await _screenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingFrame = tcs;
            if (!IsConnected) throw new IOException("ขาดการเชื่อมต่อ");

            SendJson(MsgType.ScreenRequest, new ScreenRequest(maxWidth, quality));

            byte[] jpeg;
            try
            {
                jpeg = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Dispose(); // Agent ไม่ตอบ: ถือว่าการเชื่อมต่อนี้ใช้ไม่ได้แล้ว
                throw new IOException("Agent ไม่ตอบสนอง");
            }

            using var ms = new MemoryStream(jpeg);
            using var img = Image.FromStream(ms);
            return new Bitmap(img);
        }
        finally
        {
            _pendingFrame = null;
            _screenLock.Release();
        }
    }

    public void Dispose()
    {
        IsConnected = false;
        _cts.Cancel();
        _outbox.Writer.TryComplete();
        _stream.Dispose();
        _tcp.Dispose();
    }
}
