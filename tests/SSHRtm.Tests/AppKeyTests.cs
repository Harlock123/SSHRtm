using SSHRtm.UI;

namespace SSHRtm.Tests;

public sealed class AppKeyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sshrtm-tests-").FullName;
    private readonly CancellationTokenSource _cts = new();

    public void Dispose()
    {
        _cts.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static readonly AppConfig Config = new()
    {
        PollIntervalMs = 100,
        Servers = new()
        {
            new() { Name = "web01", Host = "192.0.2.10", User = "monitor" },
            new() { Name = "db01", Host = "192.0.2.11", User = "monitor" },
            new() { Name = "cache", Host = "192.0.2.12", User = "monitor" },
        },
    };

    private App NewApp(StateStore? state = null) =>
        new(Config, state ?? new StateStore(Config.Servers.Select(s => s.Name)), _cts) { LogFolder = _dir };

    private static ConsoleKeyInfo Key(ConsoleKey key, char ch = '\0', bool shift = false) => new(ch, key, shift, false, false);

    [Fact]
    public void Space_marks_and_unmarks_the_selected_server_and_a_and_n_mark_all_and_none()
    {
        var app = NewApp();

        app.HandleKey(Key(ConsoleKey.Spacebar, ' '));
        Assert.Equal(new[] { "web01" }, app.Marked);

        app.HandleKey(Key(ConsoleKey.RightArrow));
        app.HandleKey(Key(ConsoleKey.RightArrow));
        app.HandleKey(Key(ConsoleKey.Spacebar, ' '));
        Assert.Equal(new[] { "web01", "cache" }, app.Marked);

        app.HandleKey(Key(ConsoleKey.Spacebar, ' '));
        Assert.Equal(new[] { "web01" }, app.Marked);

        app.HandleKey(Key(ConsoleKey.A, 'a'));
        Assert.Equal(new[] { "web01", "db01", "cache" }, app.Marked);

        app.HandleKey(Key(ConsoleKey.N, 'n'));
        Assert.Empty(app.Marked);
    }

    [Fact]
    public void L_in_the_grid_logs_the_marked_servers_and_keeps_logging_through_the_detail_view()
    {
        var app = NewApp();
        app.HandleKey(Key(ConsoleKey.Spacebar, ' '));
        app.HandleKey(Key(ConsoleKey.RightArrow));
        app.HandleKey(Key(ConsoleKey.Spacebar, ' '));

        app.HandleKey(Key(ConsoleKey.L, 'l'));
        var log = app.CombinedLog;
        Assert.NotNull(log);
        Assert.Equal(new[] { "web01", "db01" }, log.Servers);
        Assert.StartsWith(Path.Combine(_dir, "web01+db01-"), log.FilePath);

        // Into a detail view and back out: Esc stops only a single-server log.
        app.HandleKey(Key(ConsoleKey.Enter));
        app.HandleKey(Key(ConsoleKey.Escape));
        Assert.Same(log, app.CombinedLog);

        app.HandleKey(Key(ConsoleKey.L, 'l'));
        Assert.Null(app.CombinedLog);
        Assert.True(File.Exists(log.FilePath));
    }

    [Fact]
    public void L_with_nothing_marked_logs_the_selected_server()
    {
        var app = NewApp();
        app.HandleKey(Key(ConsoleKey.RightArrow));
        app.HandleKey(Key(ConsoleKey.L, 'l'));

        Assert.Equal(new[] { "db01" }, app.CombinedLog?.Servers);
        app.StopCombinedLogging();
    }

    [Fact]
    public async Task The_running_log_writes_rows_from_the_state_store()
    {
        var state = new StateStore(Config.Servers.Select(s => s.Name));
        foreach (var srv in Config.Servers)
        {
            state.Set(srv.Name, Fakes.Ok(srv.Name, Fakes.Snapshot(srv.Name, cores: 1, nic: "eth0"), DateTime.UtcNow));
        }

        var app = NewApp(state);
        app.HandleKey(Key(ConsoleKey.A, 'a'));
        app.StartCombinedLogging();
        var log = app.CombinedLog!;

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (log.RowsWritten < 2 && waited.Elapsed < TimeSpan.FromSeconds(10))
        {
            // Keep the snapshots fresh while the logger ticks.
            foreach (var srv in Config.Servers)
            {
                state.Set(srv.Name, state.Get(srv.Name) with { UpdatedAt = DateTime.UtcNow });
            }
            await Task.Delay(50);
        }
        app.StopCombinedLogging();

        var rows = Csv.Read(log.FilePath);
        Assert.True(rows.Count >= 3, $"expected a header and two rows, got {rows.Count} lines");
        var first = Csv.Row(rows[0], rows[1]);
        Assert.Equal("cache", first["cache::hostname"]);
        Assert.Equal("Ok", first["db01::status"]);
    }
}
