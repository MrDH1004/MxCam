using System.Text.Json;

namespace MxCam;

/// <summary>학습 기록 1건: 어떤 종류의 영상을 어떤 코덱·단계로 바꿨더니 결과가 어땠는지.</summary>
public sealed class LearnRecord
{
    public string Class { get; set; } = "";
    public VKind Kind { get; set; }
    public double Level { get; set; }
    public double Ssim { get; set; }
    /// <summary>출력 용량 / 원본 용량.</summary>
    public double SizeRatio { get; set; }
    public double Speed { get; set; }
    public DateTime At { get; set; }
    /// <summary>같은 시험(한 영상)에서 나온 기록끼리 묶는 번호.</summary>
    public string Session { get; set; } = "";
}

/// <summary>학습으로 찾은 최적 선택.</summary>
public sealed record Learned(VKind Kind, double Level, double Ratio, int Sessions);

/// <summary>학습 데이터 저장소 (%APPDATA%\MxCam\learning.json). 여러 변환 창이 동시에 써도 안전하게 잠금.</summary>
public static class Learning
{
    static string FilePath => Path.Combine(Settings.DirPath, "learning.json");
    const int MaxRecords = 5000;

    /// <summary>
    /// 영상 분류: 원본 코덱 · 해상도 대역 · 프레임 대역 · 압축 정도(픽셀당 비트) 대역.
    /// 같은 분류의 영상은 최적 코덱/단계가 비슷하다는 가정으로 학습을 재사용한다.
    /// </summary>
    public static string ClassOf(MediaInfo m)
    {
        string res = m.Height switch { <= 480 => "SD", <= 720 => "HD", <= 1080 => "FHD", <= 1440 => "QHD", _ => "UHD" };
        string fps = m.Fps switch { <= 31 => "30", <= 61 => "60", _ => "hi" };
        double bpp = m.BitsPerPixel;
        string comp = bpp switch { < 0.05 => "low", < 0.12 => "mid", < 0.3 => "high", _ => "vhigh" };
        return $"{m.VCodec}|{res}|{fps}|{comp}";
    }

    /// <summary>녹화용 분류: 해상도 대역 · 프레임 대역 (녹화 시작 전에 알 수 있는 정보).</summary>
    public static string RecClass(int w, int h, int fps)
    {
        string res = h switch { <= 480 => "SD", <= 720 => "HD", <= 1080 => "FHD", <= 1440 => "QHD", _ => "UHD" };
        string f = fps switch { <= 31 => "30", <= 61 => "60", _ => "hi" };
        return $"rec|{res}|{f}";
    }

    /// <summary>
    /// 분류별 학습 결과에서 "목표 화질(품질 단계)을 만족하는 가장 작은 코덱·단계"를 고른다.
    /// 시험(세션)마다 코덱별 최소 용량비를 구해 평균 → 가장 작은 코덱. minSpeed로 실시간 녹화 가능 여부를 거른다.
    /// </summary>
    public static Learned? Best(string cls, int level, double minSpeed = 0) => Ranking(cls, level, minSpeed).FirstOrDefault();

    /// <summary>코덱별 학습 성적 (같은 화질 기준 용량비가 작은 순).</summary>
    public static List<Learned> Ranking(string cls, int level, double minSpeed = 0)
    {
        double target = Optimizer.TargetSsim(level);
        var recs = Load().Where(r => r.Class == cls && r.Ssim >= target - 0.002 && r.Speed >= minSpeed && Codecs.IsVideoAvailable(r.Kind)).ToList();
        var perSession = recs.GroupBy(r => (r.Session, r.Kind)).Select(g => g.OrderBy(r => r.SizeRatio).First());
        return perSession.GroupBy(r => r.Kind)
            .Select(g => new Learned(g.Key, g.Average(r => r.Level), g.Average(r => r.SizeRatio), g.Select(r => r.Session).Distinct().Count()))
            .OrderBy(x => x.Ratio).ToList();
    }

    /// <summary>분류별 학습 횟수(세션 수).</summary>
    public static Dictionary<string, int> SessionCounts() =>
        Load().GroupBy(r => r.Class).ToDictionary(g => g.Key, g => g.Select(r => r.Session).Distinct().Count());

    public static void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        _cache = null;
    }

    static List<LearnRecord>? _cache;
    static DateTime _cacheStamp;

    public static List<LearnRecord> Load()
    {
        try
        {
            var stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
            if (_cache != null && stamp == _cacheStamp) return _cache;
            var list = LoadFile();
            _cache = list;
            _cacheStamp = stamp;
            return list;
        }
        catch { return LoadFile(); }
    }

    static List<LearnRecord> LoadFile()
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<LearnRecord>();
                using var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                return JsonSerializer.Deserialize<List<LearnRecord>>(fs) ?? new List<LearnRecord>();
            }
            catch (IOException) { Thread.Sleep(50); }
            catch { return new List<LearnRecord>(); }
        }
        return new List<LearnRecord>();
    }

    public static void Add(IEnumerable<LearnRecord> records)
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                Directory.CreateDirectory(Settings.DirPath);
                using var fs = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var list = fs.Length > 0 ? JsonSerializer.Deserialize<List<LearnRecord>>(fs) ?? new() : new List<LearnRecord>();
                list.AddRange(records);
                if (list.Count > MaxRecords) list.RemoveRange(0, list.Count - MaxRecords);
                fs.SetLength(0);
                JsonSerializer.Serialize(fs, list);
                return;
            }
            catch (IOException) { Thread.Sleep(100); }
            catch (JsonException)
            {
                try { File.Delete(FilePath); } catch { }
            }
        }
    }
}

public sealed record Trial(VKind Kind, double Level, double MB, double Ssim, double Speed);

public sealed record OptimizeDecision(VKind Kind, double Level, double EstMB, double Ssim, double Speed, bool NoGain,
    int HistoryCount, bool UsedHistory, List<Trial> Trials);

/// <summary>
/// 자가 학습 용량 최적화.
///  목표: 선택한 품질 단계의 목표 화질(SSIM)을 만족하면서 용량이 가장 작은 (코덱, 품질값) 찾기.
///  방법: 영상 가운데 6초 샘플을 후보 코덱마다 품질값을 바꿔 가며 인코딩 → SSIM으로 목표 화질 경계 탐색.
///  학습: 결과를 저장해 두고, 같은 분류의 영상은 성적 좋은 코덱 2개 + (20% 확률로) 다른 후보 1개만 시험.
/// </summary>
public static class Optimizer
{
    /// <summary>품질 단계 → 목표 화질 (원본 대비 SSIM). 3단계 0.975 ≈ 눈으로 구분 어려움.</summary>
    public static double TargetSsim(int level) => new[] { 0.95, 0.965, 0.975, 0.985, 0.992 }[Math.Clamp(level, 1, 5) - 1];

    static readonly VKind[] AllCandidates =
    {
        VKind.NvencHevc, VKind.NvencAv1, VKind.QsvHevc, VKind.AmfHevc, VKind.SvtAv1, VKind.X265,
        VKind.Nvenc264, VKind.Qsv264, VKind.Amf264, VKind.X264,
    };

    static readonly Random Rng = new();

    /// <param name="classOverride">학습 분류를 직접 지정 (녹화 학습: "rec|해상도|fps")</param>
    /// <param name="minSpeed">이 속도(실시간 배수) 이상인 후보만 선택 (녹화는 실시간 여유 필요)</param>
    public static OptimizeDecision Decide(string ffmpeg, MediaInfo src, Settings s, int level, IProgress<string> progress, CancellationToken ct,
        string? classOverride = null, double minSpeed = 0)
    {
        double target = TargetSsim(level);
        string cls = classOverride ?? Learning.ClassOf(src);
        string session = Guid.NewGuid().ToString("N")[..8];
        var history = Learning.Load().Where(r => r.Class == cls).ToList();
        var available = AllCandidates.Where(Codecs.IsVideoAvailable).ToList();

        // 학습된 성적: 목표 화질을 만족한 기록들의 평균 용량비 (낮을수록 좋음)
        var learned = history.Where(r => r.Ssim >= target - 0.002)
            .GroupBy(r => r.Kind)
            .Select(g => (kind: g.Key, ratio: g.Average(r => r.SizeRatio), level: g.Average(r => r.Level), n: g.Count()))
            .Where(x => available.Contains(x.kind))
            .OrderBy(x => x.ratio).ToList();

        List<VKind> toTest;
        bool usedHistory = learned.Count >= 2 && history.Count >= 4;
        if (usedHistory)
        {
            toTest = learned.Take(2).Select(x => x.kind).ToList();
            // 탐색: 가끔 다른 후보도 시험해서 더 좋은 선택이 있는지 계속 확인
            var others = available.Except(toTest).ToList();
            if (others.Count > 0 && Rng.NextDouble() < 0.2) toTest.Add(others[Rng.Next(others.Count)]);
        }
        else
        {
            // 학습 전: 효율 높은 순으로 최대 5개 (GPU 우선, 같은 계열 GPU/CPU 중복은 줄임)
            toTest = available.OrderByDescending(k => Codecs.Video(k).Efficiency).ThenByDescending(k => Codecs.Video(k).Hardware).Take(5).ToList();
        }

        var trials = new List<Trial>();
        var newRecords = new List<LearnRecord>();
        double srcMB = src.Size / 1048576.0;
        int idx = 0;
        foreach (var kind in toTest)
        {
            ct.ThrowIfCancellationRequested();
            idx++;
            var info = Codecs.Video(kind);
            // 품질 하한: SSIM은 질감 손실(뿌옇게 뭉개짐)을 잘 못 잡으므로 선택 단계보다 1단계 넘게 낮추지 않는다
            double floor = Math.Max(1, level - 1.0);
            double start = Math.Max(floor, learned.FirstOrDefault(x => x.kind == kind).level is > 0 and var l ? l : 3);
            // 품질값 이분 탐색: lo = 목표 미달 단계, hi = 목표 만족 단계
            double lo = floor - 0.5, hi = 5.5, cur = start;
            for (int step = 0; step < 4; step++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report($"[{idx}/{toTest.Count}] {info.Label} · {cur:0.0}단계 시험 중…");
                var plan = new ConvertPlan { Preset = ConvertPreset.Recommended, Video = kind, Ext = "mp4", Title = info.Label, Level = level, VideoLevel = Math.Clamp(cur, 1, 5) };
                var r = Converter.Sample(ffmpeg, src, plan, ct);
                if (r.Error != null) break;
                var t = new Trial(kind, Math.Clamp(cur, 1, 5), r.EstimatedMB, r.Ssim, r.Speed);
                trials.Add(t);
                newRecords.Add(new LearnRecord { Class = cls, Kind = kind, Level = t.Level, Ssim = t.Ssim, SizeRatio = t.MB / srcMB, Speed = t.Speed, At = DateTime.Now, Session = session });
                if (r.Ssim >= target) hi = Math.Min(hi, cur); else lo = Math.Max(lo, cur);
                if (hi - lo <= 0.6 || (cur <= floor && r.Ssim >= target) || (cur >= 5 && r.Ssim < target)) break;
                cur = hi > 5 ? Math.Min(5, cur + 1.5) : lo < floor ? Math.Max(floor, cur - 1.5) : (lo + hi) / 2;
            }
        }
        Learning.Add(newRecords);

        // 너무 느린 후보 제외: 예상 변환 시간이 영상 길이의 3배(최소 2분)를 넘으면 제외
        double maxTime = Math.Max(120, src.Duration * 3);
        var ok = trials.Where(t => t.Ssim >= target && t.Speed > 0 && t.Speed >= minSpeed && src.Duration / t.Speed <= maxTime).ToList();
        if (ok.Count == 0)
        {
            // 목표 화질을 못 맞추면 가장 화질 좋은 시도로
            var bestQ = trials.OrderByDescending(t => t.Ssim).FirstOrDefault();
            if (bestQ == null) throw new InvalidOperationException("시험 인코딩에 모두 실패했습니다.");
            return new OptimizeDecision(bestQ.Kind, bestQ.Level, bestQ.MB, bestQ.Ssim, bestQ.Speed, bestQ.MB >= srcMB * 0.92, history.Count, usedHistory, trials);
        }
        var best = ok.OrderBy(t => t.MB).First();
        return new OptimizeDecision(best.Kind, best.Level, best.MB, best.Ssim, best.Speed, best.MB >= srcMB * 0.92, history.Count, usedHistory, trials);
    }
}
