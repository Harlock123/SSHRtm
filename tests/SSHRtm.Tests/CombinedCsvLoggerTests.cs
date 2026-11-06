using SSHRtm.Metrics;

namespace SSHRtm.Tests;

public sealed class CombinedCsvLoggerTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 6, 14, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Directory.CreateTempSubdirectory("sshrtm-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Both_servers_reporting_writes_one_row_per_tick_with_prefixed_columns()
    {
        var path = PathFor("both.csv");
        var states = new Dictionary<string, ServerState>
        {
            ["web01"] = Fakes.Ok("web01", Fakes.Snapshot("web01", cores: 2, nic: "eth0"), T0),
            ["db01"] = Fakes.Ok("db01", Fakes.Snapshot("db01", cores: 4, nic: "ens3"), T0),
        };

        using (var log = new CombinedCsvLogger(path, new[] { "web01", "db01" }))
        {
            log.Write(T0.AddSeconds(0.5), n => states[n]);
            log.Write(T0.AddSeconds(1.5), n => states[n]);
        }

        var rows = Csv.Read(path);
        var header = rows[0];
        Assert.Equal(3, rows.Count);
        Assert.Equal("timestamp", header[0]);
        Assert.Equal(new[] { "web01::status", "web01::sample_age_s", "web01::hostname" }, header[1..4]);
        Assert.Contains("web01::cpu1_pct", header);
        Assert.Contains("web01::net_eth0_rx_bps", header);
        Assert.Contains("db01::cpu3_pct", header);
        Assert.Contains("db01::net_ens3_rx_bps", header);
        Assert.DoesNotContain("web01::cpu2_pct", header); // web01 has two cores, so cpu0 and cpu1 only
        Assert.True(header.IndexOf("web01::proc20_command") < header.IndexOf("db01::status"), "each server's columns sit together");

        var first = Csv.Row(header, rows[1]);
        Assert.Equal("Ok", first["web01::status"]);
        Assert.Equal("0.50", first["web01::sample_age_s"]);
        Assert.Equal("web01", first["web01::hostname"]);
        Assert.Equal("db01", first["db01::hostname"]);
        Assert.Matches(@"^\d{8}-\d{6}\.\d{3}$", first["timestamp"]);
        Assert.All(rows, r => Assert.Equal(header.Count, r.Count));
    }

    [Fact]
    public void Header_waits_for_a_late_server_then_adds_its_columns_and_pads_earlier_rows()
    {
        var path = PathFor("late.csv");
        var states = new Dictionary<string, ServerState>
        {
            ["web01"] = Fakes.Ok("web01", Fakes.Snapshot("web01", cores: 1, nic: "eth0"), T0),
            ["db01"] = new("db01", ServerStatus.Connecting, null, null, T0),
        };
        var options = new CombinedLogOptions { HeaderWait = TimeSpan.FromSeconds(3), StaleAfter = TimeSpan.FromHours(1) };

        using (var log = new CombinedCsvLogger(path, new[] { "web01", "db01" }, options))
        {
            log.Write(T0.AddSeconds(1), n => states[n]);
            log.Write(T0.AddSeconds(2), n => states[n]);
            Assert.Equal(0, log.RowsWritten); // still waiting for db01
            log.Write(T0.AddSeconds(4), n => states[n]); // the wait is over: header without db01's metrics
            Assert.Equal(3, log.RowsWritten);

            states["db01"] = Fakes.Ok("db01", Fakes.Snapshot("db01", cores: 2, nic: "ens3"), T0.AddSeconds(5));
            log.Write(T0.AddSeconds(5), n => states[n]);
            log.Write(T0.AddSeconds(6), n => states[n]);
        }

        var rows = Csv.Read(path);
        var header = rows[0];
        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.Equal(header.Count, r.Count));
        Assert.Equal("db01::status", header[header.IndexOf("web01::proc20_command") + 1]);
        Assert.True(header.IndexOf("db01::hostname") > header.IndexOf("db01::sample_age_s"));

        var early = Csv.Row(header, rows[1]);
        Assert.Equal("Connecting", early["db01::status"]);
        Assert.Equal("", early["db01::hostname"]);
        Assert.Equal("", early["db01::sample_age_s"]);
        Assert.Equal("web01", early["web01::hostname"]);

        var late = Csv.Row(header, rows[4]);
        Assert.Equal("Ok", late["db01::status"]);
        Assert.Equal("db01", late["db01::hostname"]);
        Assert.Equal("0.00", late["db01::sample_age_s"]);
    }

    [Fact]
    public void A_server_in_error_or_with_a_stale_snapshot_leaves_its_cells_empty()
    {
        var path = PathFor("gaps.csv");
        var web = Fakes.Snapshot("web01", cores: 1, nic: "eth0");
        var states = new Dictionary<string, ServerState>
        {
            ["web01"] = Fakes.Ok("web01", web, T0),
            ["db01"] = Fakes.Ok("db01", Fakes.Snapshot("db01", cores: 1, nic: "eth0"), T0),
        };
        var options = new CombinedLogOptions { StaleAfter = TimeSpan.FromSeconds(5) };

        using (var log = new CombinedCsvLogger(path, new[] { "web01", "db01" }, options))
        {
            log.Write(T0.AddSeconds(1), n => states[n]);
            states["db01"] = new("db01", ServerStatus.Error, "Connection refused", states["db01"].Snapshot, T0.AddSeconds(2));
            log.Write(T0.AddSeconds(2), n => states[n]);
            log.Write(T0.AddSeconds(9), n => states[n]); // web01's snapshot is now 9 s old
        }

        var rows = Csv.Read(path);
        var header = rows[0];
        var errorRow = Csv.Row(header, rows[2]);
        Assert.Equal("Error", errorRow["db01::status"]);
        Assert.Equal("", errorRow["db01::cpu_overall_pct"]);
        Assert.Equal("", errorRow["db01::sample_age_s"]);
        Assert.Equal("web01", errorRow["web01::hostname"]);

        var staleRow = Csv.Row(header, rows[3]);
        Assert.Equal("Ok", staleRow["web01::status"]);
        Assert.Equal("", staleRow["web01::cpu_overall_pct"]);
    }

    [Fact]
    public void Process_slots_can_be_left_out()
    {
        var path = PathFor("noprocs.csv");
        var state = Fakes.Ok("web01", Fakes.Snapshot("web01", cores: 1, nic: "eth0"), T0);

        using (var log = new CombinedCsvLogger(path, new[] { "web01" }, new CombinedLogOptions { IncludeProcesses = false }))
        {
            log.Write(T0, _ => state);
        }

        var header = Csv.Read(path)[0];
        Assert.DoesNotContain(header, h => h.Contains("proc", StringComparison.Ordinal));
        Assert.Contains("web01::fs_/_used_pct", header);
    }

    [Fact]
    public void Stopping_before_the_header_wait_ends_still_writes_the_rows()
    {
        var path = PathFor("short.csv");
        var states = new Dictionary<string, ServerState>
        {
            ["web01"] = Fakes.Ok("web01", Fakes.Snapshot("web01", cores: 1, nic: "eth0"), T0),
            ["db01"] = new("db01", ServerStatus.Connecting, null, null, T0),
        };

        using (var log = new CombinedCsvLogger(path, new[] { "web01", "db01" }))
        {
            log.Write(T0.AddSeconds(1), n => states[n]);
        }

        var rows = Csv.Read(path);
        Assert.Equal(2, rows.Count);
        Assert.Contains("db01::status", rows[0]);
    }

    [Fact]
    public void Commas_and_quotes_in_process_names_stay_in_their_cell()
    {
        var path = PathFor("quotes.csv");
        var snap = Fakes.Snapshot("web01", cores: 1, nic: "eth0") with
        {
            TopProcs = new List<ProcessInfo> { new(42, 12.5, 1.25, "java,\"worker\"") },
        };

        using (var log = new CombinedCsvLogger(path, new[] { "web01" }))
        {
            log.Write(T0, _ => Fakes.Ok("web01", snap, T0));
        }

        var rows = Csv.Read(path);
        var row = Csv.Row(rows[0], rows[1]);
        Assert.Equal("java,\"worker\"", row["web01::proc1_command"]);
        Assert.Equal("12.50", row["web01::proc1_cpu_pct"]);
    }

    [Theory]
    [InlineData(new[] { "web01" }, "web01-20261106-140000.csv")]
    [InlineData(new[] { "web01", "db01", "cache" }, "web01+db01+cache-20261106-140000.csv")]
    [InlineData(new[] { "a", "b", "c", "d" }, "group-4servers-20261106-140000.csv")]
    [InlineData(new[] { "rack:1/web" }, "rack_1_web-20261106-140000.csv")]
    public void File_names_name_the_servers(string[] servers, string expected)
        => Assert.Equal(expected, CombinedCsvLogger.DefaultFileName(servers, new DateTime(2026, 11, 6, 14, 0, 0)));

    [Fact]
    public void The_single_server_log_keeps_its_layout()
    {
        string path;
        using (var log = new CsvLogger(_dir, "web01"))
        {
            log.Write(Fakes.Snapshot("web01", cores: 2, nic: "eth0"));
            path = log.FilePath;
        }

        var rows = Csv.Read(path);
        var header = rows[0];
        Assert.Equal(new[] { "hostname", "num_cpu", "uptime_sec", "load_1m" }, header[..4]);
        Assert.Equal("timestamp", header[^1]);
        Assert.Equal("proc20_command", header[^2]);
        Assert.DoesNotContain(header, h => h.Contains("::", StringComparison.Ordinal));
        Assert.Matches(@"^\d{8}-\d{6}$", rows[1][^1]);
    }
}
