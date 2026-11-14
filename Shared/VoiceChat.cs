using System.Collections.Concurrent;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RainDrop.Shared;

/// <summary>
/// เครื่องยนต์คอลเสียงสด: จับเสียงจากไมโครโฟนส่งออก และเล่นเสียงของคนอื่นหลายคนพร้อมกัน
/// ใช้ NAudio: WaveInEvent จับเสียง, MixingSampleProvider ผสมเสียงทุกคนแล้วเล่นผ่าน WaveOutEvent
/// เสียงเป็น PCM 16-bit mono 16 kHz เฟรมละ ~20 ms (640 ไบต์)
/// </summary>
public sealed class VoiceChat : IDisposable
{
    private readonly WaveFormat _format = new(HubProtocol.VoiceSampleRate, 16, 1);
    private readonly Action<byte[]> _onCaptured;          // ส่ง pcm ที่จับได้ออกไป
    private readonly object _gate = new();

    private WaveInEvent? _mic;
    private IWavePlayer? _speaker;
    private MixingSampleProvider? _mixer;
    private readonly ConcurrentDictionary<int, BufferedWaveProvider> _incoming = new();

    public bool Active { get; private set; }
    public bool Muted { get; set; }

    public VoiceChat(Action<byte[]> onCaptured) => _onCaptured = onCaptured;

    /// <summary>เริ่มจับและเล่นเสียง (เรียกตอนเข้าช่องเสียง) โยน exception ถ้าไม่มีอุปกรณ์เสียง</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (Active) return;

            // ตัวผสมเสียง: 16 kHz mono แบบ float, ReadFully เพื่อไม่ให้หยุดเมื่อไม่มีใครพูด
            _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(HubProtocol.VoiceSampleRate, 1))
            {
                ReadFully = true,
            };
            _speaker = new WaveOutEvent { DesiredLatency = 120 };
            _speaker.Init(_mixer);
            _speaker.Play();

            _mic = new WaveInEvent { WaveFormat = _format, BufferMilliseconds = 20 };
            _mic.DataAvailable += OnMicData;
            _mic.StartRecording();

            Active = true;
        }
    }

    private void OnMicData(object? sender, WaveInEventArgs e)
    {
        if (Muted || !Active) return;
        var pcm = new byte[e.BytesRecorded];
        Array.Copy(e.Buffer, pcm, e.BytesRecorded);
        try { _onCaptured(pcm); } catch { /* การเชื่อมต่อขาด */ }
    }

    /// <summary>มีเสียงจากสมาชิกคนหนึ่งเข้ามา เพิ่มเข้า buffer ของคนนั้นเพื่อนำไปผสม</summary>
    public void Play(int senderId, byte[] pcm)
    {
        if (!Active) return;
        var buffer = _incoming.GetOrAdd(senderId, _ =>
        {
            var bwp = new BufferedWaveProvider(_format)
            {
                DiscardOnBufferOverflow = true,
                BufferDuration = TimeSpan.FromSeconds(3),
            };
            lock (_gate) _mixer?.AddMixerInput(bwp.ToSampleProvider());
            return bwp;
        });
        buffer.AddSamples(pcm, 0, pcm.Length);
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!Active) return;
            Active = false;

            if (_mic is not null)
            {
                _mic.DataAvailable -= OnMicData;
                try { _mic.StopRecording(); } catch { }
                _mic.Dispose();
                _mic = null;
            }
            if (_speaker is not null)
            {
                try { _speaker.Stop(); } catch { }
                _speaker.Dispose();
                _speaker = null;
            }
            _mixer = null;
            _incoming.Clear();
        }
    }

    public void Dispose() => Stop();
}
