using System.Text.Json;

namespace MxCam;

public enum RecordMode { Screen, Audio, Gif }

public enum LimitAction { None, Restart, Exit, Shutdown }

public enum WatermarkPos { BottomRight, BottomLeft, TopRight, TopLeft, Center }

public sealed class BenchResult
{
    public VKind Kind { get; set; }
    public string Label { get; set; } = "";
    /// <summary>품질 1~5단계별 실측 용량(MB/분)과 화질(SSIM).</summary>
    public double[] Mb { get; set; } = new double[5];
    public double[] Ssim { get; set; } = new double[5];
    /// <summary>5단계 중 가장 느린 인코딩 속도(실시간 대비 배수).</summary>
    public double Speed { get; set; }
    public bool Realtime { get; set; }
    public string? Error { get; set; }

    // 아래는 선택 단계에 따라 Recommender.Decide가 다시 계산 (저장 불필요)
    /// <summary>기준 화질에 맞췄을 때의 환산 용량 — 코덱 간 공정 비교용.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public double MBAtTarget { get; set; }
    /// <summary>기준 화질을 내는 이 코덱의 (소수) 품질 단계.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public double CalibratedLevel { get; set; }
    /// <summary>0 = 정확, -1 = 1단계도 기준보다 고화질(용량 상한), 1 = 5단계도 기준 화질 미달.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public int State { get; set; }
}

public sealed class Settings
{
    public string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "MxCam");
    public string FfmpegPath { get; set; } = "";
    public string CodecId { get; set; } = Codecs.AutoId;
    public string AudioCodecId { get; set; } = "a_mp3";
    public int Level { get; set; } = 3;
    public int Fps { get; set; } = 30;
    public bool RecordCursor { get; set; } = true;
    public bool SystemAudio { get; set; } = true;
    public bool Microphone { get; set; }
    public string? MicDeviceId { get; set; }
    public RecordMode Mode { get; set; } = RecordMode.Screen;
    public int RegionX { get; set; } = 200;
    public int RegionY { get; set; } = 200;
    public int RegionW { get; set; } = 1280;
    public int RegionH { get; set; } = 720;
    public bool AttachToolbar { get; set; }
    public int ToolbarX { get; set; } = int.MinValue;
    public int ToolbarY { get; set; } = int.MinValue;
    public string ScreenshotFormat { get; set; } = "png";
    public Keys RecordKey { get; set; } = Keys.F2;
    public Keys PauseKey { get; set; } = Keys.F3;
    public Keys CaptureKey { get; set; } = Keys.F11;
    /// <summary>녹화 시간 제한(초, 0 = 없음)과 제한 후 동작.</summary>
    public int TimeLimitSeconds { get; set; }
    public LimitAction TimeLimitAction { get; set; } = LimitAction.None;
    public bool OpenFolderAfterRecord { get; set; }
    public bool MinimizeWhileRecording { get; set; }

    /// <summary>파일 이름 = 접두어_날짜_시간.확장자</summary>
    public string FilePrefix { get; set; } = "MxCam";
    /// <summary>출력 샘플레이트 (0 = 장치 원본), 채널 (1 = 모노, 2 = 스테레오).</summary>
    public int AudioSampleRate { get; set; } = 48000;
    public int AudioChannels { get; set; } = 2;

    public bool ClickEffect { get; set; }
    /// <summary>동영상 변환: 분석 후 추천대로 바로 변환.</summary>
    public bool AutoConvert { get; set; }
    /// <summary>시작할 때 GitHub 새 버전 확인·자동 다운로드.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>탐색기에서 비디오 파일 우클릭 → "MxCam: 용량 최적화 변환" 메뉴.</summary>
    public bool ShellMenu { get; set; } = true;
    /// <summary>녹화가 끝나면 그 화면으로 코덱을 자가 학습하고, 자동 모드·변환 추천에 학습 결과를 쓴다.</summary>
    public bool AutoLearn { get; set; } = true;
    public string WatermarkPath { get; set; } = "";
    public int WatermarkOpacity { get; set; } = 70;
    public WatermarkPos WatermarkPosition { get; set; } = WatermarkPos.BottomRight;

    /// <summary>실측 추천 결과 (자동 모드가 우선 사용).</summary>
    public List<BenchResult> Bench { get; set; } = new();
    /// <summary>실측 당시 가로×세로×fps (다른 크기의 예상 용량 환산용).</summary>
    public long BenchPixelRate { get; set; }
    public DateTime? BenchAt { get; set; }

    /// <summary>내 PC 분석 결과 (처음 한 번 자동, 이후 옵션에서 다시 분석).</summary>
    public string? PcFingerprint { get; set; }
    public string? PcSummary { get; set; }
    public VKind? PcBest { get; set; }

    public Rectangle Region
    {
        get => new(RegionX, RegionY, RegionW, RegionH);
        set { RegionX = value.X; RegionY = value.Y; RegionW = value.Width; RegionH = value.Height; }
    }

    // MXCAM_SETTINGS_DIR: UI 자동 테스트가 사용자 설정을 건드리지 않도록 분리
    static string Dir => Environment.GetEnvironmentVariable("MXCAM_SETTINGS_DIR") is { Length: > 0 } d
        ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MxCam");
    public static string DirPath => Dir;
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();

            // 이전 이름(ymxCam) 설정 이어받기
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ymxCam", "settings.json");
            if (Environment.GetEnvironmentVariable("MXCAM_SETTINGS_DIR") == null && File.Exists(old)
                && JsonSerializer.Deserialize<Settings>(File.ReadAllText(old)) is { } s)
            {
                var oldVideos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ymxCam");
                if (string.Equals(s.OutputDir, oldVideos, StringComparison.OrdinalIgnoreCase)) s.OutputDir = new Settings().OutputDir;
                if (s.FilePrefix == "ymxCam") s.FilePrefix = "MxCam";
                s.Save();
                return s;
            }
        }
        catch { /* 손상된 설정은 기본값으로 */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }
}
