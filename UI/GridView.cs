using Spectre.Console;
using Spectre.Console.Rendering;

namespace SSHRtm.UI;

public static class GridView
{
    // Standard tile is a 5-line body + 2 border lines = 7 rows tall, needs ~40 cols wide.
    // Compact tile is a 1-line body + 2 border lines = 3 rows tall, needs ~24 cols wide.
    private const int StandardTileWidth = 40;
    private const int StandardTileHeight = 7;
    private const int CompactTileWidth = 24;
    private const int CompactTileHeight = 3;

    public static int ComputeCols(int consoleWidth, bool compact)
    {
        var minW = compact ? CompactTileWidth : StandardTileWidth;
        return Math.Max(1, consoleWidth / minW);
    }

    public static int ComputeRows(int availableHeight, bool compact)
    {
        var h = compact ? CompactTileHeight : StandardTileHeight;
        return Math.Max(1, availableHeight / h);
    }

    public static IRenderable Render(
        StateStore state,
        IReadOnlyList<ServerConfig> servers,
        int selectedIndex,
        int consoleWidth,
        int availableHeight,
        bool compact,
        int gridOffset)
    {
        var cols = ComputeCols(consoleWidth, compact);
        var rows = ComputeRows(availableHeight, compact);
        var pageSize = Math.Max(1, cols * rows);

        var offset = Math.Clamp(gridOffset, 0, Math.Max(0, servers.Count - pageSize));
        var end = Math.Min(servers.Count, offset + pageSize);

        var grid = new Grid();
        for (int c = 0; c < cols; c++) grid.AddColumn();

        var rowBuf = new IRenderable[cols];
        var idx = 0;
        for (int i = offset; i < end; i++)
        {
            rowBuf[idx++] = BuildTile(state.Get(servers[i].Name), i == selectedIndex, compact);
            if (idx == cols)
            {
                grid.AddRow(rowBuf);
                rowBuf = new IRenderable[cols];
                idx = 0;
            }
        }
        if (idx > 0)
        {
            for (int j = idx; j < cols; j++) rowBuf[j] = new Markup(string.Empty);
            grid.AddRow(rowBuf);
        }

        return grid;
    }

    public static IRenderable RenderHeader(string mode, string focusedServer, string focusedPanel, bool compact, string? logFileName = null)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss");
        string keys;
        string left;
        if (mode == "grid")
        {
            var tileMode = compact ? "compact" : "standard";
            keys = $"[{Theme.Dim}]arrows[/] nav  [{Theme.Dim}]Enter[/] detail  [{Theme.Dim}]1-9[/] jump  [{Theme.Dim}]c[/] tiles  [{Theme.Dim}]q[/] quit";
            left = $"[{Theme.Accent}]SSHRtm[/] [{Theme.Dim}]{ts}[/]  view:[{Theme.Header}]{mode}[/]  tiles:[{Theme.Header}]{tileMode}[/]  focus:[{Theme.Selected}]{Markup.Escape(focusedServer)}[/]";
        }
        else
        {
            keys = $"[{Theme.Dim}]Esc[/] back  [{Theme.Dim}]Tab[/] panel  [{Theme.Dim}]↑↓[/] scroll  [{Theme.Dim}]PgUp/Dn[/] page  [{Theme.Dim}]Home/End[/] jump  [{Theme.Dim}]L[/] log  [{Theme.Dim}]q[/] quit";
            var logTag = logFileName is null
                ? string.Empty
                : $"  [{Theme.Crit}]REC[/] [{Theme.Header}]{Markup.Escape(logFileName)}[/]";
            left = $"[{Theme.Accent}]SSHRtm[/] [{Theme.Dim}]{ts}[/]  view:[{Theme.Header}]{mode}[/]  server:[{Theme.Selected}]{Markup.Escape(focusedServer)}[/]  panel:[{Theme.Selected}]{focusedPanel}[/]{logTag}";
        }
        return new Markup($"{left}    {keys}");
    }

    private static IRenderable BuildTile(ServerState entry, bool selected, bool compact)
        => compact ? BuildCompactTile(entry, selected) : BuildStandardTile(entry, selected);

    private static IRenderable BuildStandardTile(ServerState entry, bool selected)
    {
        var borderColor = selected ? Theme.Selected : Theme.Border;
        var titleColor = selected ? Theme.Selected : Theme.Header;

        string body;
        if (entry.Status == ServerStatus.Connecting)
        {
            body = $"[{Theme.Warn}]connecting...[/]";
        }
        else if (entry.Status == ServerStatus.Error || entry.Snapshot is null)
        {
            var msg = entry.LastError ?? "no data";
            if (msg.Length > 60) msg = msg.Substring(0, 57) + "...";
            body = $"[{Theme.Error}]ERR:[/] {Markup.Escape(msg)}";
        }
        else
        {
            var snap = entry.Snapshot;
            var cpuPct = snap.CpuPct.GetValueOrDefault("cpu");
            var memPct = snap.Memory.UsedPct;
            var totalRx = snap.NetRate.Values.Sum(n => n.RxBps);
            var totalTx = snap.NetRate.Values.Sum(n => n.TxBps);
            var maxBusy = snap.DiskRate.Values.Count > 0
                ? snap.DiskRate.Values.Max(d => d.BusyPct) : 0;
            var load = string.Join(' ', snap.LoadAvg);
            var up = Theme.FormatUptime(snap.UptimeSec);

            body = string.Join('\n',
                $"CPU  {Theme.BarMarkup(cpuPct, 22)}",
                $"MEM  {Theme.BarMarkup(memPct, 22)}",
                $"NET  [{Theme.Accent}]{Theme.FormatRate(totalRx),12}[/] Rx  [{Theme.Accent}]{Theme.FormatRate(totalTx),12}[/] Tx",
                $"DISK {Theme.BarMarkup(maxBusy, 22)}",
                $"[{Theme.Dim}]load {load}  up {up}  cores {snap.NumCpu}[/]");
        }

        return new Panel(new Markup(body))
        {
            Header = new PanelHeader($"[{titleColor}]{Markup.Escape(entry.Name)}[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(borderColor),
            Expand = true,
        };
    }

    private static IRenderable BuildCompactTile(ServerState entry, bool selected)
    {
        var borderColor = selected ? Theme.Selected : Theme.Border;
        var titleColor = selected ? Theme.Selected : Theme.Header;

        string body;
        if (entry.Status == ServerStatus.Connecting)
        {
            body = $"[{Theme.Warn}]connecting...[/]";
        }
        else if (entry.Status == ServerStatus.Error || entry.Snapshot is null)
        {
            var msg = entry.LastError ?? "no data";
            if (msg.Length > 20) msg = msg.Substring(0, 17) + "...";
            body = $"[{Theme.Error}]ERR:[/] {Markup.Escape(msg)}";
        }
        else
        {
            var snap = entry.Snapshot;
            var cpuPct = snap.CpuPct.GetValueOrDefault("cpu");
            var memPct = snap.Memory.UsedPct;
            var maxBusy = snap.DiskRate.Values.Count > 0
                ? snap.DiskRate.Values.Max(d => d.BusyPct) : 0;
            body = $"C{Theme.MicroBar(cpuPct)} M{Theme.MicroBar(memPct)} D{Theme.MicroBar(maxBusy)}";
        }

        return new Panel(new Markup(body))
        {
            Header = new PanelHeader($"[{titleColor}]{Markup.Escape(entry.Name)}[/]"),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(borderColor),
            Expand = true,
            Padding = new Padding(0, 0, 0, 0),
        };
    }
}
