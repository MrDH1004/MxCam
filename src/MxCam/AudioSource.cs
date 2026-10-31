using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MxCam;

/// <summary>
/// WASAPI 소리 입력.
/// 시스템 소리는 loopback으로 받는데, 아무 소리도 안 날 때 패킷이 끊겨 싱크가 밀리는 것을 막으려고
/// 같은 장치에 무음을 계속 재생한다. 받은 샘플 수가 곧 녹화의 기준 시계가 된다.
/// </summary>
public sealed class AudioSource : IDisposable
{
    readonly WasapiCapture _capture;
    readonly WasapiOut? _silence;
    volatile bool _paused;
    long _bytes;
    readonly System.Diagnostics.Stopwatch _clk = System.Diagnostics.Stopwatch.StartNew();
    long _lastPacketTicks;
    double _lastReturned;
    readonly object _clockLock = new();

    public string Name { get; }
    public WaveFormat Format { get; }
    public string FfmpegFormat { get; }
    public event Action<byte[], int>? Data;
    public string? Error { get; private set; }

    AudioSource(WasapiCapture capture, WasapiOut? silence, string name)
    {
        _capture = capture;
        _silence = silence;
        Name = name;
        Format = capture.WaveFormat;
        FfmpegFormat = SampleFormat(Format);
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => { if (e.Exception != null) Error = e.Exception.Message; };
    }

    public static AudioSource SystemLoopback()
    {
        using var en = new MMDeviceEnumerator();
        var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var cap = new WasapiLoopbackCapture(dev);
        WasapiOut? silence = null;
        try
        {
            silence = new WasapiOut(dev, AudioClientShareMode.Shared, false, 100);
            silence.Init(new SilenceProvider(cap.WaveFormat));
        }
        catch
        {
            silence?.Dispose();
            silence = null;
        }
        return new AudioSource(cap, silence, "시스템 소리: " + dev.FriendlyName);
    }

    public static AudioSource Microphone(string? deviceId)
    {
        using var en = new MMDeviceEnumerator();
        MMDevice? dev = null;
        if (!string.IsNullOrEmpty(deviceId))
        {
            try { dev = en.GetDevice(deviceId); } catch { }
        }
        foreach (var role in new[] { Role.Communications, Role.Console, Role.Multimedia })
        {
            if (dev != null) break;
            try { dev = en.GetDefaultAudioEndpoint(DataFlow.Capture, role); } catch { }
        }
        dev ??= en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault()
                ?? throw new InvalidOperationException("연결된 마이크가 없습니다.");
        return new AudioSource(new WasapiCapture(dev, false, 20), null, "마이크: " + dev.FriendlyName);
    }

    public static List<(string id, string name)> ListMicrophones()
    {
        var list = new List<(string, string)>();
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                list.Add((d.ID, d.FriendlyName));
        }
        catch { }
        return list;
    }

    static string SampleFormat(WaveFormat wf)
    {
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (wf is WaveFormatExtensible ext && ext.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        if (isFloat) return wf.BitsPerSample == 64 ? "f64le" : "f32le";
        return wf.BitsPerSample switch { 8 => "u8", 16 => "s16le", 24 => "s24le", _ => "s32le" };
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (_paused || e.BytesRecorded <= 0) return;
        var copy = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, copy, 0, e.BytesRecorded);
        Interlocked.Add(ref _bytes, e.BytesRecorded);
        Interlocked.Exchange(ref _lastPacketTicks, _clk.ElapsedTicks);
        Data?.Invoke(copy, copy.Length);
    }

    /// <summary>일시정지 중 들어온 소리는 버린다 → 기준 시계도 멈춘다.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!value) Interlocked.Exchange(ref _lastPacketTicks, 0);
        }
    }

    /// <summary>
    /// 녹화 기준 시계(초) = 저장한 소리 길이. WASAPI는 수십 ms 단위로 몰아서 전달하므로
    /// 마지막 패킷 이후 경과 시간(최대 150ms)으로 보간하고 단조 증가를 보장한다.
    /// </summary>
    public double Seconds
    {
        get
        {
            double raw = Interlocked.Read(ref _bytes) / (double)Format.AverageBytesPerSecond;
            long last = Interlocked.Read(ref _lastPacketTicks);
            double v = raw;
            if (!_paused && last != 0)
                v += Math.Min((_clk.ElapsedTicks - last) / (double)System.Diagnostics.Stopwatch.Frequency, 0.15);
            lock (_clockLock)
            {
                if (v < _lastReturned) v = _lastReturned;
                _lastReturned = v;
            }
            return v;
        }
    }

    /// <summary>보간 없이 실제로 저장된 소리 길이(초).</summary>
    public double RawSeconds => Interlocked.Read(ref _bytes) / (double)Format.AverageBytesPerSecond;

    public void Start()
    {
        _silence?.Play();
        _capture.StartRecording();
    }

    public void Stop()
    {
        try { _capture.StopRecording(); } catch { }
        try { _silence?.Stop(); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
        _silence?.Dispose();
    }
}
