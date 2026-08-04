using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace StreamerKit;

public enum ServerState { Stopped, Starting, Running, Stopping, Restarting, Failed }

/// <summary>
/// Supervises one child server: launch, capture stdout/stderr, sniff the URL it prints,
/// and tear the whole process tree down again.
/// </summary>
public sealed partial class ServerProcess : INotifyPropertyChanged, IServerCard
{
    private const int LogCapacity = 40_000;   // characters kept per server

    /// <summary>Give up after this many crashes in a row, so a server that cannot start
    /// (port taken, missing dependency) doesn't restart forever.</summary>
    public const int MaxRestarts = 5;

    /// <summary>Seconds to wait before each successive restart.</summary>
    private static readonly int[] Backoff = { 2, 5, 10, 20, 30 };

    /// <summary>Survive this long and the crash counter resets - it was a healthy run.</summary>
    private static readonly TimeSpan HealthyUptime = TimeSpan.FromSeconds(60);

    private readonly ServerConfig _config;
    private readonly DispatcherQueue _ui;
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly StringBuilder _log = new();

    private Process? _process;
    private ServerState _state = ServerState.Stopped;
    private string? _url;

    private DispatcherQueueTimer? _restartTimer;   // field, or it stops ticking when collected
    private DateTime _startedAt;
    private bool _stopRequested;
    private int _crashes;
    private int _secondsUntilRestart;

    /// <summary>Restart automatically after an unexpected exit. Driven by Settings.</summary>
    public bool AutoRestart { get; set; } = true;

    public ServerProcess(ServerConfig config)
    {
        _config = config;
        _ui = DispatcherQueue.GetForCurrentThread();
    }

    public string Name => _config.Name;
    public string? Subtitle => _config.Subtitle;
    public ServerConfig Config => _config;

    public bool AutoStart
    {
        get => _config.AutoStart;
        set
        {
            if (_config.AutoStart == value) return;
            _config.AutoStart = value;
            Notify(nameof(AutoStart));
        }
    }

    public ServerState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            Notify(nameof(State), nameof(StatusText), nameof(StatusBrush), nameof(ActionText), nameof(IsBusy));
        }
    }

    public string? Url
    {
        get => _url;
        private set
        {
            if (_url == value) return;
            _url = value;
            Notify(nameof(Url), nameof(HasUrl), nameof(UrlVisibility));
        }
    }

    public bool HasUrl => !string.IsNullOrEmpty(_url);
    public Visibility UrlVisibility => HasUrl ? Visibility.Visible : Visibility.Collapsed;
    public bool IsBusy => State is ServerState.Starting or ServerState.Stopping or ServerState.Restarting;
    public string LogText => _log.ToString();

    public string StatusText => State switch
    {
        ServerState.Starting => "Starting",
        ServerState.Running => "Running",
        ServerState.Stopping => "Stopping",
        ServerState.Restarting => $"Crashed · restarting in {_secondsUntilRestart}s ({_crashes}/{MaxRestarts})",
        ServerState.Failed => _crashes >= MaxRestarts ? $"Failed · gave up after {_crashes} crashes" : "Failed",
        _ => "Stopped"
    };

    /// <summary>System status colours, so the dot matches whatever Windows theme is in use.</summary>
    public Brush StatusBrush => State switch
    {
        ServerState.Running => Theme("SystemFillColorSuccessBrush", 0x3D, 0xD6, 0x8C),
        ServerState.Starting or ServerState.Stopping or ServerState.Restarting
            => Theme("SystemFillColorCautionBrush", 0xE3, 0xB3, 0x41),
        ServerState.Failed => Theme("SystemFillColorCriticalBrush", 0xF0, 0x59, 0x6B),
        _ => Theme("TextFillColorDisabledBrush", 0x6E, 0x6E, 0x6E)
    };

    public string ActionText => State is ServerState.Stopped or ServerState.Failed ? "Start" : "Stop";

    private static Brush Theme(string key, byte r, byte g, byte b)
    {
        if (Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Brush brush)
            return brush;
        return new SolidColorBrush(Color.FromArgb(255, r, g, b));
    }

    public void Toggle()
    {
        if (State is ServerState.Stopped or ServerState.Failed) Start();
        else Stop();
    }

    public void Start() => Start(userInitiated: true);

    private void Start(bool userInitiated)
    {
        if (_process is { HasExited: false }) return;

        CancelPendingRestart();
        _stopRequested = false;

        if (userInitiated)
        {
            // A manual start is a clean slate; an automatic one keeps the crash history visible.
            _crashes = 0;
            _log.Clear();
            Notify(nameof(LogText));
        }

        Url = null;

        var (exe, args) = ResolveCommand();
        if (exe is null)
        {
            Append($"'{_config.Command}' was not found on PATH.");
            if (_config.FallbackCommand is { } fb) Append($"'{fb}' was not found either.");
            State = ServerState.Failed;
            return;
        }

        if (!Directory.Exists(_config.WorkingDirectory))
        {
            Append($"Working directory does not exist: {_config.WorkingDirectory}");
            State = ServerState.Failed;
            return;
        }

        var info = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = _config.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        // Otherwise Python buffers stdout when it isn't a console and the URL shows up minutes late.
        info.Environment["PYTHONUNBUFFERED"] = "1";
        info.Environment["PYTHONIOENCODING"] = "utf-8";

        try
        {
            State = ServerState.Starting;
            Append($"> {exe} {args}");

            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => OnOutput(e.Data);
            process.ErrorDataReceived += (_, e) => OnOutput(e.Data);
            process.Exited += (_, _) => OnExited(process);

            process.Start();
            Native.AdoptChild(process);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            _startedAt = DateTime.Now;

            // If it survives the first couple of seconds without printing a URL, call it running.
            _ = Task.Delay(2000).ContinueWith(_ => _ui.TryEnqueue(() =>
            {
                if (State == ServerState.Starting && _process is { HasExited: false })
                    State = ServerState.Running;
            }));
        }
        catch (Exception ex)
        {
            Append(ex.Message);
            State = ServerState.Failed;
        }
    }

    public void Stop()
    {
        _stopRequested = true;
        CancelPendingRestart();

        var process = _process;
        if (process is null || process.HasExited)
        {
            if (State == ServerState.Restarting) Append("Restart cancelled.");
            _crashes = 0;
            State = ServerState.Stopped;
            FlushLog();
            return;
        }

        State = ServerState.Stopping;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Append(ex.Message);
        }
    }

    /// <summary>Blocking stop used on window close so nothing is left listening on a port.</summary>
    public void StopAndWait(int millisecondsTimeout = 3000)
    {
        _stopRequested = true;      // closing down: an exit now is not a crash
        CancelPendingRestart();

        var process = _process;
        if (process is null || process.HasExited) return;
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(millisecondsTimeout);
        }
        catch { /* already gone */ }
    }

    /// <summary>Drains buffered output into the bound log. Called on a UI timer so bursts don't thrash XAML.</summary>
    public void FlushLog()
    {
        if (_incoming.IsEmpty) return;

        while (_incoming.TryDequeue(out var line))
        {
            _log.Append(line).Append('\n');
            if (Url is null && UrlPattern().Match(line) is { Success: true } m)
            {
                Url = m.Value.TrimEnd('.', ',', ')');
                if (State == ServerState.Starting) State = ServerState.Running;
            }
        }

        if (_log.Length > LogCapacity)
            _log.Remove(0, _log.Length - LogCapacity);

        Notify(nameof(LogText));
    }

    private void OnOutput(string? line)
    {
        if (line is not null) _incoming.Enqueue(line);
    }

    private void OnExited(Process process)
    {
        _ui.TryEnqueue(() =>
        {
            if (!ReferenceEquals(process, _process)) return;

            var code = process.ExitCode;
            var uptime = DateTime.Now - _startedAt;
            _process = null;
            process.Dispose();
            Url = null;

            if (_stopRequested)
            {
                Append("Stopped.");
                _crashes = 0;
                State = ServerState.Stopped;
                FlushLog();
                return;
            }

            // Nobody asked it to stop, so it fell over - even on exit code 0, because these
            // are long-running servers and quitting on their own is already wrong.
            if (uptime > HealthyUptime) _crashes = 0;   // it had been healthy; start counting fresh
            _crashes++;

            Append($"Server exited unexpectedly with code {code} after {uptime.TotalSeconds:0}s.");
            WriteCrashLog(code, uptime);

            if (!AutoRestart)
            {
                Append("Auto-restart is off, leaving it stopped.");
                State = ServerState.Failed;
                FlushLog();
                return;
            }

            if (_crashes > MaxRestarts)
            {
                Append($"Gave up after {MaxRestarts} restarts - something is wrong that restarting won't fix. "
                     + "Check the output above, then press Start once it's sorted.");
                State = ServerState.Failed;
                FlushLog();
                return;
            }

            ScheduleRestart();
        });
    }

    private void ScheduleRestart()
    {
        _secondsUntilRestart = Backoff[Math.Min(_crashes - 1, Backoff.Length - 1)];
        State = ServerState.Restarting;
        Append($"Restarting in {_secondsUntilRestart}s (attempt {_crashes} of {MaxRestarts}).");
        FlushLog();

        _restartTimer = _ui.CreateTimer();
        _restartTimer.Interval = TimeSpan.FromSeconds(1);
        _restartTimer.Tick += (timer, _) =>
        {
            _secondsUntilRestart--;
            Notify(nameof(StatusText));
            if (_secondsUntilRestart > 0) return;

            timer.Stop();
            Start(userInitiated: false);
        };
        _restartTimer.Start();
    }

    private void CancelPendingRestart()
    {
        _restartTimer?.Stop();
        _restartTimer = null;
    }

    /// <summary>Appends the crash to crashes.log next to the exe, with the tail of the output.</summary>
    private void WriteCrashLog(int exitCode, TimeSpan uptime)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "crashes.log");

            // Keep the file bounded; drop the oldest half when it gets large.
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
            {
                var kept = File.ReadAllLines(path);
                File.WriteAllLines(path, kept.Skip(kept.Length / 2));
            }

            var report = new StringBuilder()
                .AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {Name} exited with code {exitCode} "
                          + $"after {uptime.TotalSeconds:0}s (crash {_crashes} of {MaxRestarts})");

            foreach (var line in _log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(12))
                report.AppendLine($"    {line}");

            File.AppendAllText(path, report.AppendLine().ToString());
        }
        catch
        {
            // A read-only folder shouldn't turn a server crash into an app crash.
        }
    }

    /// <summary>Picks the first configured command that actually exists on PATH.</summary>
    private (string? exe, string args) ResolveCommand()
    {
        if (Which(_config.Command) is { } primary)
            return (primary, _config.Arguments);

        if (!string.IsNullOrWhiteSpace(_config.FallbackCommand) && Which(_config.FallbackCommand!) is { } fallback)
            return (fallback, _config.FallbackArguments ?? _config.Arguments);

        return (null, "");
    }

    private static string? Which(string command)
    {
        if (Path.IsPathRooted(command))
            return File.Exists(command) ? command : null;

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';');
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in extensions.Prepend(""))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), command + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { /* malformed PATH entry */ }
            }
        }
        return null;
    }

    private void Append(string line)
    {
        _incoming.Enqueue(line);
        FlushLog();
    }

    [GeneratedRegex(@"https?://[^\s""'<>]+")]
    private static partial Regex UrlPattern();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(params string[] names)
    {
        foreach (var name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
