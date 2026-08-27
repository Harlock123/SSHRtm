using Spectre.Console;
using Spectre.Console.Rendering;

namespace SSHRtm.UI;

public static class DetailView
{
    public static IRenderable Render(ServerState entry, int windowHeight, int focus, int[] scroll)
    {
        if (entry.Status != ServerStatus.Ok || entry.Snapshot is null)
        {
            var msg = entry.LastError ?? "no data yet";
            return new Panel(new Markup($"[{Theme.Error}]{Markup.Escape(msg)}[/]"))
            {
                Header = new PanelHeader($"[{Theme.Header}]{Markup.Escape(entry.Name)}[/]"),
                Border = BoxBorder.Rounded,
                Expand = true,
            };
        }

        var snap = entry.Snapshot;

        // Two independent stacked columns so nothing pads to match its neighbor:
        //   Left : CPU  (natural)  →  NET  (≤5 rows)  →  FS     (absorbs rest)
        //   Right: MEM  (fixed 5)  →  DISK (≤5 rows)  →  PROCS  (absorbs rest)
        var usable = Math.Max(15, windowHeight - 3);

        var netN  = Math.Max(1, snap.NetRate.Count);
        var diskN = Math.Max(1, snap.DiskRate.Count);
        var fsN   = Math.Max(1, snap.Filesystems.Count);
        var procN = Math.Max(1, snap.TopProcs.Count);

        // Right column: MEM (5+2), DISK IO (up to 5+4), PROCS gets the rest.
        var diskShown = Math.Min(diskN, 5);
        var diskCell  = Math.Max(6, Math.Max(2, diskShown) + 4);
        const int memCell = 7;
        var procsMax  = Math.Max(3, usable - memCell - diskCell - 4);
        var procsShown = Math.Min(procN, procsMax);

        // Left column: CPU at natural size (ALL + one row per core), NET ≤ 5,
        // FS takes the rest so mountpoints show without scrolling when they fit.
        var netShown = Math.Min(netN, 5);
        var netCell  = Math.Max(6, Math.Max(2, netShown) + 4);
        var cpuContent = 1 + Math.Max(1, snap.NumCpu);
        var cpuCell = cpuContent + 2;
        var fsMax = Math.Max(3, usable - cpuCell - netCell - 2);
        if (fsMax < 3)
        {
            // Not enough room even for a few mounts — steal from CPU.
            cpuCell = Math.Max(7, usable - netCell - 5);
            cpuContent = cpuCell - 2;
            fsMax = Math.Max(3, usable - cpuCell - netCell - 2);
        }
        var fsShown = Math.Min(fsN, fsMax);

        var cpuPanel   = BuildCpuPanel(snap, cpuContent, focus == App.FocusCpu, scroll[App.FocusCpu]);
        var memPanel   = BuildMemPanel(snap);
        var netPanel   = BuildNetPanel(snap, netShown, focus == App.FocusNet, scroll[App.FocusNet]);
        var diskPanel  = BuildDiskPanel(snap, diskShown, focus == App.FocusDisk, scroll[App.FocusDisk]);
        var fsPanel    = BuildFsPanel(snap, fsShown, focus == App.FocusFs, scroll[App.FocusFs]);
        var procsPanel = BuildProcsPanel(snap, procsShown, focus == App.FocusProcs, scroll[App.FocusProcs]);

        var left = new Grid().AddColumn();
        left.AddRow(cpuPanel);
        left.AddRow(netPanel);
        left.AddRow(fsPanel);

        var right = new Grid().AddColumn();
        right.AddRow(memPanel);
        right.AddRow(diskPanel);
        right.AddRow(procsPanel);

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow(left, right);

        return grid;
    }

    private static IRenderable BuildCpuPanel(Metrics.Snapshot snap, int rows, bool focused, int offset)
    {
        var lines = new List<string>();
        var overall = snap.CpuPct.GetValueOrDefault("cpu");
        // "ALL" always occupies the first line; scrolling operates on the core list.
        lines.Add($"[bold]ALL[/]  {Theme.BarMarkup(overall, 30)}");

        var cores = snap.CpuPct.Keys
            .Where(k => k.StartsWith("cpu") && k != "cpu")
            .OrderBy(k => int.TryParse(k.AsSpan(3), out var i) ? i : 0)
            .ToList();

        var visible = Math.Max(1, rows - 1);   // reserve 1 line for ALL
        var (from, to, clamped) = ClampWindow(offset, cores.Count, visible);
        for (int i = from; i < to; i++)
        {
            lines.Add($"{cores[i],-5} {Theme.BarMarkup(snap.CpuPct[cores[i]], 24)}");
        }

        var title = MakeTitle("CPU", clamped, visible, cores.Count, focused);
        return MakePanel(title, string.Join('\n', lines), focused);
    }

    private static IRenderable BuildMemPanel(Metrics.Snapshot snap)
    {
        var m = snap.Memory;
        var body = string.Join('\n',
            $"MEM  {Theme.BarMarkup(m.UsedPct, 30)}",
            $"[{Theme.Dim}]used {Theme.FormatBytes(m.Used)} / {Theme.FormatBytes(m.Total)}   cache {Theme.FormatBytes(m.Cached)}[/]",
            "",
            $"SWAP {Theme.BarMarkup(m.SwapPct, 30)}",
            $"[{Theme.Dim}]used {Theme.FormatBytes(m.SwapUsed)} / {Theme.FormatBytes(m.SwapTotal)}[/]");
        return MakePanel($"[{Theme.Accent}]MEMORY[/]", body, focused: false);
    }

    private static IRenderable BuildNetPanel(Metrics.Snapshot snap, int rows, bool focused, int offset)
    {
        var all = snap.NetRate.OrderBy(kv => kv.Key).ToList();
        var (from, to, clamped) = ClampWindow(offset, all.Count, rows);
        if (all.Count == 0)
        {
            return MakePanel($"[{Theme.Accent}]NETWORK[/]", $"[{Theme.Dim}]no interfaces[/]", focused);
        }

        var table = new Table().Border(TableBorder.Simple).Expand()
            .AddColumn("Iface").AddColumn("Rx").AddColumn("Tx").AddColumn("Rx pkt/s").AddColumn("Tx pkt/s");
        for (int i = from; i < to; i++)
        {
            var (iface, r) = (all[i].Key, all[i].Value);
            table.AddRow(
                Markup.Escape(iface),
                Theme.FormatRate(r.RxBps),
                Theme.FormatRate(r.TxBps),
                $"{r.RxPps:N0}",
                $"{r.TxPps:N0}");
        }
        PadTable(table, rows - (to - from), 5);

        return WrapTable("NETWORK", table, clamped, rows, all.Count, focused);
    }

    private static IRenderable BuildDiskPanel(Metrics.Snapshot snap, int rows, bool focused, int offset)
    {
        var all = snap.DiskRate.OrderBy(kv => kv.Key).ToList();
        var (from, to, clamped) = ClampWindow(offset, all.Count, rows);
        if (all.Count == 0)
        {
            return MakePanel($"[{Theme.Accent}]DISK IO[/]", $"[{Theme.Dim}]no disks[/]", focused);
        }

        var table = new Table().Border(TableBorder.Simple).Expand()
            .AddColumn("Device").AddColumn("Read").AddColumn("Write").AddColumn("Busy").AddColumn("InFlight");
        for (int i = from; i < to; i++)
        {
            var (dev, d) = (all[i].Key, all[i].Value);
            table.AddRow(
                Markup.Escape(dev),
                Theme.FormatRate(d.ReadBps),
                Theme.FormatRate(d.WriteBps),
                $"{d.BusyPct:N1}%",
                $"{d.InFlight}");
        }
        PadTable(table, rows - (to - from), 5);

        return WrapTable("DISK IO", table, clamped, rows, all.Count, focused);
    }

    private static IRenderable BuildFsPanel(Metrics.Snapshot snap, int rows, bool focused, int offset)
    {
        var all = snap.Filesystems;
        var (from, to, clamped) = ClampWindow(offset, all.Count, rows);
        if (all.Count == 0)
        {
            return MakePanel($"[{Theme.Accent}]FILESYSTEMS[/]", $"[{Theme.Dim}]none[/]", focused);
        }

        var lines = new List<string>();
        for (int i = from; i < to; i++)
        {
            var fs = all[i];
            var mount = fs.Mount.Length > 22 ? fs.Mount.Substring(0, 22) : fs.Mount;
            lines.Add($"{Markup.Escape(mount),-22} {Theme.BarMarkup(fs.UsedPct, 16)} [{Theme.Dim}]({Markup.Escape(fs.Type)})[/]");
        }
        for (int i = to - from; i < rows; i++) lines.Add(string.Empty);
        var title = MakeTitle("FILESYSTEMS", clamped, rows, all.Count, focused);
        return MakePanel(title, string.Join('\n', lines), focused);
    }

    private static IRenderable BuildProcsPanel(Metrics.Snapshot snap, int rows, bool focused, int offset)
    {
        var all = snap.TopProcs;
        var (from, to, clamped) = ClampWindow(offset, all.Count, rows);
        if (all.Count == 0)
        {
            return MakePanel($"[{Theme.Accent}]TOP PROCS[/]", $"[{Theme.Dim}]no procs[/]", focused);
        }

        var table = new Table().Border(TableBorder.Simple).Expand()
            .AddColumn("PID").AddColumn("CPU%").AddColumn("MEM%").AddColumn("Command");
        for (int i = from; i < to; i++)
        {
            var p = all[i];
            var cmd = p.Command.Length > 36 ? p.Command.Substring(0, 36) : p.Command;
            table.AddRow($"{p.Pid}", $"{p.Cpu:N1}", $"{p.Mem:N1}", Markup.Escape(cmd));
        }
        PadTable(table, rows - (to - from), 4);

        return WrapTable("TOP PROCS", table, clamped, rows, all.Count, focused);
    }

    private static void PadTable(Table table, int missing, int columnCount)
    {
        if (missing <= 0) return;
        var blanks = new string[columnCount];
        for (int i = 0; i < columnCount; i++) blanks[i] = string.Empty;
        for (int i = 0; i < missing; i++) table.AddRow(blanks);
    }

    private static (int from, int to, int clampedOffset) ClampWindow(int offset, int total, int visible)
    {
        if (total <= 0 || visible <= 0) return (0, 0, 0);
        var maxOffset = Math.Max(0, total - visible);
        var off = Math.Clamp(offset, 0, maxOffset);
        return (off, Math.Min(total, off + visible), off);
    }

    private static string MakeTitle(string label, int offset, int visible, int total, bool focused)
    {
        var color = focused ? Theme.Selected : Theme.Accent;
        var scroll = "";
        if (total > visible && total > 0)
        {
            var end = Math.Min(total, offset + visible);
            scroll = $" [{Theme.Dim}]{offset + 1}-{end}/{total}[/]";
        }
        return $"[{color}]{label}[/]{scroll}";
    }

    private static IRenderable WrapTable(string label, Table table, int offset, int visible, int total, bool focused)
    {
        var title = MakeTitle(label, offset, visible, total, focused);
        return MakePanel(title, table, focused);
    }

    private static Panel MakePanel(string titleMarkup, string body, bool focused)
        => WrapPanel(titleMarkup, new Markup(body), focused);

    private static Panel MakePanel(string titleMarkup, IRenderable body, bool focused)
        => WrapPanel(titleMarkup, body, focused);

    private static Panel WrapPanel(string titleMarkup, IRenderable body, bool focused)
    {
        var border = focused ? Theme.Selected : Theme.Border;
        return new Panel(body)
        {
            Header = new PanelHeader(titleMarkup),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(border),
            Expand = true,
        };
    }
}
