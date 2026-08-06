using Spectre.Console;
using Spectre.Console.Rendering;
using SSHRtm.Metrics;

namespace SSHRtm.UI;

public sealed class App
{
    // Detail-mode focus indexes match DetailView panel order.
    public const int FocusCpu = 0;
    public const int FocusNet = 1;
    public const int FocusDisk = 2;
    public const int FocusFs = 3;
    public const int FocusProcs = 4;
    public const int FocusCount = 5;

    private static readonly string[] FocusNames = { "CPU", "NETWORK", "DISK", "FILESYSTEMS", "PROCS" };

    // xterm-style DEC private modes:
    //   ?1049h/l  enter/leave alternate screen buffer
    //   ?25h/l    show/hide cursor
    //   2J        clear entire screen
    //   H         move cursor to top-left
    private const string EnterAltScreen = "[?1049h[?25l[H";
    private const string LeaveAltScreen = "[?25h[?1049l";
    private const string ClearAndHome   = "[2J[H";

    private readonly AppConfig _config;
    private readonly StateStore _state;
    private readonly CancellationTokenSource _cts;
    private int _selected;
    private string _mode = "grid";

    private int _detailFocus = FocusCpu;
    private readonly int[] _detailScroll = new int[FocusCount];

    private bool _compact;
    private int _gridOffset;

    // CSV logging session — non-null iff logging is currently active.
    private CsvLogger? _logger;
    private CancellationTokenSource? _loggerCts;
    private Task? _loggerTask;

    public App(AppConfig config, StateStore state, CancellationTokenSource cts)
    {
        _config = config;
        _state = state;
        _cts = cts;
    }

    public async Task RunAsync()
    {
        // Alt screen: no scrollback means the mouse wheel and PgUp can't scroll
        // the live region away, and we don't touch the user's original terminal.
        Console.Write(EnterAltScreen);
        try
        {
            // Outer loop restarts the Live display whenever the console dims
            // change. Live keeps internal bookkeeping about how many lines it
            // drew last frame; when the terminal shrinks below that count
            // (Ctrl-+ font zoom is the common trigger) the incremental
            // move-up-and-rewrite math lands in the wrong place and the display
            // never recovers. Tearing Live down and rebuilding it at the new
            // size resets that state cleanly.
            while (!_cts.IsCancellationRequested)
            {
                var startW = SafeWidth();
                var startH = SafeHeight();
                AnsiConsole.Console.Profile.Width = startW;
                AnsiConsole.Console.Profile.Height = startH;
                Console.Write(ClearAndHome);

                var sizeChanged = false;

                await AnsiConsole.Live(BuildRenderable())
                    .Overflow(VerticalOverflow.Crop)
                    .Cropping(VerticalOverflowCropping.Bottom)
                    .AutoClear(false)
                    .StartAsync(async ctx =>
                    {
                        while (!_cts.IsCancellationRequested)
                        {
                            DrainKeys();

                            var w = SafeWidth();
                            var h = SafeHeight();
                            if (w != startW || h != startH)
                            {
                                sizeChanged = true;
                                return;
                            }

                            ctx.UpdateTarget(BuildRenderable());
                            ctx.Refresh();
                            try { await Task.Delay(_config.RenderIntervalMs, _cts.Token); }
                            catch (OperationCanceledException) { break; }
                        }
                    });

                if (sizeChanged)
                {
                    // Brief settle so we don't churn Live once per intermediate
                    // frame while the user is still dragging or holding Ctrl-+.
                    try { await Task.Delay(80, _cts.Token); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            StopLogging();
            Console.Write(LeaveAltScreen);
        }
    }

    private static int SafeWidth()
    {
        try { return Math.Max(20, Console.WindowWidth); }
        catch { return Math.Max(20, AnsiConsole.Console.Profile.Width); }
    }

    private static int SafeHeight()
    {
        try { return Math.Max(10, Console.WindowHeight); }
        catch { return Math.Max(10, AnsiConsole.Console.Profile.Height); }
    }

    private IRenderable BuildRenderable()
    {
        var selected = Math.Clamp(_selected, 0, _config.Servers.Count - 1);
        var focusedName = _config.Servers[selected].Name;
        var logFile = _logger is not null && _logger.ServerName == focusedName
            ? Path.GetFileName(_logger.FilePath)
            : null;
        var header = GridView.RenderHeader(_mode, focusedName, FocusNames[_detailFocus], _compact, logFile);
        var width = Math.Max(20, AnsiConsole.Console.Profile.Width);
        var height = Math.Max(10, AnsiConsole.Console.Profile.Height);
        var body = _mode == "grid"
            ? GridView.Render(_state, _config.Servers, selected, width, height - 1, _compact, _gridOffset)
            : DetailView.Render(_state.Get(focusedName), height, _detailFocus, _detailScroll);
        return new Rows(header, body);
    }

    private void DrainKeys()
    {
        while (Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true);
            HandleKey(key);
        }
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        if (_mode == "detail")
        {
            HandleDetailKey(key);
            return;
        }

        var cols = GridView.ComputeCols(SafeWidth(), _compact);

        switch (key.Key)
        {
            case ConsoleKey.Q:
                _cts.Cancel();
                break;
            case ConsoleKey.Escape:
                _cts.Cancel();
                break;
            case ConsoleKey.Enter:
                _mode = "detail";
                _detailFocus = FocusCpu;
                Array.Clear(_detailScroll);
                break;
            case ConsoleKey.UpArrow:
                if (_selected >= cols) _selected -= cols;
                EnsureSelectedVisible();
                break;
            case ConsoleKey.DownArrow:
                if (_selected + cols < _config.Servers.Count) _selected += cols;
                EnsureSelectedVisible();
                break;
            case ConsoleKey.LeftArrow:
                if (_selected > 0) _selected--;
                EnsureSelectedVisible();
                break;
            case ConsoleKey.RightArrow:
                if (_selected < _config.Servers.Count - 1) _selected++;
                EnsureSelectedVisible();
                break;
            case ConsoleKey.Tab:
                _selected = (_selected + 1) % _config.Servers.Count;
                EnsureSelectedVisible();
                break;
            case ConsoleKey.C:
                _compact = !_compact;
                _gridOffset = 0;
                EnsureSelectedVisible();
                break;
            case ConsoleKey.PageUp:
                {
                    var page = PageSize();
                    _selected = Math.Max(0, _selected - page);
                    EnsureSelectedVisible();
                }
                break;
            case ConsoleKey.PageDown:
                {
                    var page = PageSize();
                    _selected = Math.Min(_config.Servers.Count - 1, _selected + page);
                    EnsureSelectedVisible();
                }
                break;
            case ConsoleKey.Home:
                _selected = 0;
                EnsureSelectedVisible();
                break;
            case ConsoleKey.End:
                _selected = _config.Servers.Count - 1;
                EnsureSelectedVisible();
                break;
            default:
                if (key.KeyChar >= '1' && key.KeyChar <= '9')
                {
                    var n = key.KeyChar - '1';
                    if (n < _config.Servers.Count) _selected = n;
                    EnsureSelectedVisible();
                }
                break;
        }
    }

    private int PageSize()
    {
        var cols = GridView.ComputeCols(SafeWidth(), _compact);
        var rows = GridView.ComputeRows(SafeHeight() - 1, _compact);
        return Math.Max(1, cols * rows);
    }

    private void EnsureSelectedVisible()
    {
        var cols = GridView.ComputeCols(SafeWidth(), _compact);
        var rows = GridView.ComputeRows(SafeHeight() - 1, _compact);
        var pageSize = Math.Max(1, cols * rows);

        if (_selected < _gridOffset)
        {
            _gridOffset = (_selected / cols) * cols;
        }
        else if (_selected >= _gridOffset + pageSize)
        {
            var firstVisibleRow = (_selected / cols) - rows + 1;
            _gridOffset = Math.Max(0, firstVisibleRow * cols);
        }

        var maxOffset = Math.Max(0, ((_config.Servers.Count - 1) / cols) * cols - (rows - 1) * cols);
        _gridOffset = Math.Clamp(_gridOffset, 0, Math.Max(0, maxOffset));
    }

    private void HandleDetailKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Q:
                _cts.Cancel();
                break;
            case ConsoleKey.Escape:
                // Leaving the detail view closes any active log file, as
                // requested — re-entering + pressing L again starts a new file
                // with a fresh timestamp.
                StopLogging();
                _mode = "grid";
                break;
            case ConsoleKey.L:
                ToggleLogging();
                break;
            case ConsoleKey.Tab:
                if ((key.Modifiers & ConsoleModifiers.Shift) != 0)
                    _detailFocus = (_detailFocus + FocusCount - 1) % FocusCount;
                else
                    _detailFocus = (_detailFocus + 1) % FocusCount;
                break;
            case ConsoleKey.UpArrow:
                AdjustScroll(-1);
                break;
            case ConsoleKey.DownArrow:
                AdjustScroll(+1);
                break;
            case ConsoleKey.PageUp:
                AdjustScroll(-5);
                break;
            case ConsoleKey.PageDown:
                AdjustScroll(+5);
                break;
            case ConsoleKey.Home:
                _detailScroll[_detailFocus] = 0;
                break;
            case ConsoleKey.End:
                _detailScroll[_detailFocus] = Math.Max(0, GetFocusedPanelTotal() - 1);
                break;
        }
    }

    private void AdjustScroll(int delta)
    {
        var total = GetFocusedPanelTotal();
        var max = Math.Max(0, total - 1);
        _detailScroll[_detailFocus] = Math.Clamp(_detailScroll[_detailFocus] + delta, 0, max);
    }

    private void ToggleLogging()
    {
        if (_logger is not null) StopLogging();
        else                     StartLogging();
    }

    private void StartLogging()
    {
        var selected = Math.Clamp(_selected, 0, _config.Servers.Count - 1);
        var serverName = _config.Servers[selected].Name;
        var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        try
        {
            _logger = new CsvLogger(dir, serverName);
        }
        catch
        {
            _logger = null;
            return;
        }

        _loggerCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var logger = _logger;
        var ct = _loggerCts.Token;
        _loggerTask = Task.Run(() => LogLoop(logger, serverName, ct));
    }

    private void StopLogging()
    {
        var cts = _loggerCts;
        var task = _loggerTask;
        var logger = _logger;
        _loggerCts = null;
        _loggerTask = null;
        _logger = null;

        try { cts?.Cancel(); } catch { }
        try { task?.Wait(500); } catch { }
        cts?.Dispose();
        logger?.Dispose();
    }

    private async Task LogLoop(CsvLogger logger, string serverName, CancellationToken ct)
    {
        Snapshot? last = null;
        // Sample rate matches poll rate (capped at 500 ms so a slow poll config
        // still checks reasonably often). We use reference-identity on Snapshot
        // to skip duplicate writes between polls.
        var tickMs = Math.Clamp(_config.PollIntervalMs, 100, 5000);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var st = _state.Get(serverName);
                if (st.Snapshot is not null && !ReferenceEquals(st.Snapshot, last))
                {
                    logger.Write(st.Snapshot);
                    last = st.Snapshot;
                }
            }
            catch
            {
                // Never let a log-write blow up the TUI. Losing a row is fine.
            }

            try { await Task.Delay(tickMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private int GetFocusedPanelTotal()
    {
        var name = _config.Servers[Math.Clamp(_selected, 0, _config.Servers.Count - 1)].Name;
        var snap = _state.Get(name).Snapshot;
        if (snap is null) return 0;
        return _detailFocus switch
        {
            FocusCpu   => snap.CpuPct.Keys.Count(k => k.StartsWith("cpu") && k != "cpu"),
            FocusNet   => snap.NetRate.Count,
            FocusDisk  => snap.DiskRate.Count,
            FocusFs    => snap.Filesystems.Count,
            FocusProcs => snap.TopProcs.Count,
            _          => 0,
        };
    }
}
