using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace MxCam;

public static class Ffmpeg
{
    /// <summary>설정 경로 → 실행 파일 옆 → PATH → winget 설치 위치 순으로 ffmpeg.exe를 찾는다.</summary>
    public static string? Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        var winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
        {
            try
            {
                var hit = Directory.EnumerateFiles(winget, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
            catch { }
        }
        return null;
    }

    public static string ProbePath(string ffmpeg) => Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");

    public static (int code, string output) RunCapture(string exe, IEnumerable<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return (-1, sb + "\n(timeout)");
        }
        p.WaitForExit();
        return (p.ExitCode, sb.ToString());
    }
}

/// <summary>백그라운드에서 돌아가는 ffmpeg 프로세스. stderr는 비동기로 비워서 파이프 막힘을 방지.</summary>
public sealed class FfmpegProcess : IDisposable
{
    readonly Process _p;
    readonly LinkedList<string> _log = new();

    public FfmpegProcess(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        CommandLine = exe + " " + string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        _p = Process.Start(psi)!;
        _p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_log)
            {
                _log.AddLast(e.Data);
                if (_log.Count > 60) _log.RemoveFirst();
            }
        };
        _p.OutputDataReceived += (_, _) => { };
        _p.BeginErrorReadLine();
        _p.BeginOutputReadLine();
        try { _p.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
    }

    public string CommandLine { get; }
    public bool HasExited => _p.HasExited;
    public int ExitCode => _p.ExitCode;

    public string Log
    {
        get { lock (_log) return string.Join(Environment.NewLine, _log); }
    }

    public bool WaitForExit(int ms)
    {
        if (!_p.WaitForExit(ms)) return false;
        _p.WaitForExit();
        return true;
    }

    public void Kill()
    {
        try { if (!_p.HasExited) _p.Kill(true); } catch { }
    }

    public void Dispose()
    {
        Kill();
        _p.Dispose();
    }
}

/// <summary>
/// ffmpeg 입력용 named pipe. 전용 스레드가 큐를 비우며 쓴다 (캡처와 인코딩 스레드 분리).
/// 같은 버퍼를 repeat 번 써서 누락 프레임을 복제한다.
/// </summary>
public sealed class PipeFeeder : IDisposable
{
    readonly record struct Item(byte[] Buffer, int Length, int Repeat, Action? Release);

    readonly NamedPipeServerStream _server;
    readonly BlockingCollection<Item> _queue;
    readonly CancellationTokenSource _cts = new();
    readonly Thread _thread;

    public string Path { get; }
    public volatile bool Failed;
    public string? Error;
    public long BytesWritten;

    public PipeFeeder(string name, int boundedCapacity)
    {
        Path = $@"\\.\pipe\{name}";
        _server = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
        _queue = boundedCapacity > 0 ? new BlockingCollection<Item>(boundedCapacity) : new BlockingCollection<Item>();
        _thread = new Thread(Run) { IsBackground = true, Name = "pipe " + name, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    void Run()
    {
        try
        {
            _server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
            foreach (var item in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    for (int i = 0; i < item.Repeat; i++)
                    {
                        _server.Write(item.Buffer, 0, item.Length);
                        BytesWritten += item.Length;
                    }
                }
                finally { item.Release?.Invoke(); }
            }
            _server.Flush();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Error = ex.Message;
            Failed = true;
        }
        finally
        {
            while (_queue.TryTake(out var rest)) rest.Release?.Invoke();
            try { _server.Dispose(); } catch { }
        }
    }

    public bool TryAdd(byte[] buffer, int length, int repeat, Action? release, int timeoutMs)
    {
        if (Failed || _queue.IsAddingCompleted) return false;
        try { return _queue.TryAdd(new Item(buffer, length, repeat, release), timeoutMs); }
        catch (InvalidOperationException) { return false; }
    }

    public void Add(byte[] buffer, int length)
    {
        if (Failed || _queue.IsAddingCompleted) return;
        try { _queue.Add(new Item(buffer, length, 1, null)); } catch (InvalidOperationException) { }
    }

    /// <summary>더 쓸 데이터 없음 → 큐를 다 쓴 뒤 파이프를 닫아 ffmpeg에 EOF 전달.</summary>
    public void Complete() { try { _queue.CompleteAdding(); } catch { } }

    public bool Join(int ms) => _thread.Join(ms);

    public void Abort()
    {
        Complete();
        _cts.Cancel();
    }

    public void Dispose()
    {
        Abort();
        _thread.Join(2000);
        _cts.Dispose();
    }
}
