using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace RainDrop.Shared;

/// <summary>
/// ค้นหาเครื่องลูกอัตโนมัติด้วย UDP broadcast (พอร์ตเดียวกับ TCP คือ 11400)
///   Master  --broadcast-->  DiscoveryRequest  (มีลายนิ้วมือกุญแจของ Master)
///   Agent   --unicast---->  DiscoveryReply    (ตอบเฉพาะเมื่อลายนิ้วมือตรงกับกุญแจของตัวเอง)
/// จึงเจอเฉพาะเครื่องลูกที่ติดตั้งจากชุดเดียวกัน และ Agent ไม่ตอบใครก็ได้ที่สุ่มสแกนมา
/// ข้อจำกัด: broadcast ข้ามเราเตอร์/VLAN ไม่ได้ เครื่องที่อยู่คนละวงยังต้องเพิ่มด้วย IP เอง
/// </summary>
public static class Discovery
{
    public const string RequestMagic = "RAINDROP_DISCOVER_V1";
    public const string ReplyMagic = "RAINDROP_HERE_V1";

    // ปิดพฤติกรรมของ Windows ที่ทำให้ ReceiveAsync โยน error 10054 เมื่อปลายทางปิดพอร์ต
    private const int SIO_UDP_CONNRESET = -1744830452;

    public record Request(string Magic, string Fingerprint);
    public record Reply(string Magic, string MachineName, string UserName, int Port);
    public record Found(IPAddress Address, Reply Reply);

    private static void DisableConnReset(UdpClient udp)
    {
        try { udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null); }
        catch { /* ไม่ใช่ Windows หรือไม่รองรับ */ }
    }

    // ====================== ฝั่ง Agent ======================

    /// <summary>รอรับคำค้นหาและตอบกลับ ทำงานจนกว่าจะถูกยกเลิก</summary>
    public static async Task RespondAsync(int udpPort, string fingerprint, int tcpPort, Action<string> onError, CancellationToken ct)
    {
        UdpClient udp;
        try
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
            DisableConnReset(udp);
        }
        catch (Exception ex)
        {
            onError($"เปิดพอร์ตค้นหาอัตโนมัติ (UDP {udpPort}) ไม่ได้: {ex.Message}");
            return;
        }

        using (udp)
        {
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                try
                {
                    var request = JsonSerializer.Deserialize<Request>(received.Buffer);
                    if (request?.Magic != RequestMagic || request.Fingerprint != fingerprint) continue;

                    var reply = JsonSerializer.SerializeToUtf8Bytes(
                        new Reply(ReplyMagic, Environment.MachineName, Environment.UserName, tcpPort));
                    await udp.SendAsync(reply, received.RemoteEndPoint, ct);
                }
                catch
                {
                    // แพ็กเก็ตผิดรูปแบบ: ข้าม
                }
            }
        }
    }

    // ====================== ฝั่ง Master ======================

    /// <summary>ส่ง broadcast ไปทุกวงเครือข่ายที่เครื่องนี้ต่ออยู่ แล้วรอคำตอบตามเวลาที่กำหนด</summary>
    public static async Task<List<Found>> ScanAsync(int udpPort, string fingerprint, TimeSpan wait)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        DisableConnReset(udp);

        var request = JsonSerializer.SerializeToUtf8Bytes(new Request(RequestMagic, fingerprint));
        foreach (var target in BroadcastAddresses())
        {
            try { await udp.SendAsync(request, new IPEndPoint(target, udpPort)); }
            catch { /* บางการ์ดเครือข่ายส่ง broadcast ไม่ได้: ข้าม */ }
        }

        var results = new Dictionary<string, Found>();
        using var cts = new CancellationTokenSource(wait);
        while (true)
        {
            try
            {
                var received = await udp.ReceiveAsync(cts.Token);
                var reply = JsonSerializer.Deserialize<Reply>(received.Buffer);
                if (reply?.Magic == ReplyMagic)
                    results[received.RemoteEndPoint.Address.ToString()] = new Found(received.RemoteEndPoint.Address, reply);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // แพ็กเก็ตผิดรูปแบบ: รอต่อ
            }
        }
        return results.Values.ToList();
    }

    /// <summary>255.255.255.255 และ directed broadcast ของทุกวง เช่น 192.168.1.255</summary>
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var list = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null) continue;

                var ip = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var broadcast = new byte[4];
                for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                list.Add(new IPAddress(broadcast));
            }
        }
        return list;
    }
}
