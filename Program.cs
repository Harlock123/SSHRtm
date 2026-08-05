using Spectre.Console;
using SSHRtm;
using SSHRtm.UI;

if (args.Contains("--help") || args.Contains("-h"))
{
    PrintHelp();
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

AppConfig config;
Dictionary<string, string> creds;

var adHocHost = GetArg("--host") ?? GetArg("--server");
if (adHocHost is not null)
{
    var user = GetArg("--user");
    if (user is null)
    {
        AnsiConsole.MarkupLine("[red]--host requires --user[/]");
        return 1;
    }
    var portStr = GetArg("--port");
    var port = portStr is not null && int.TryParse(portStr, out var p) ? p : 22;
    var name = GetArg("--name") ?? adHocHost;
    var password = GetArg("--password") ?? AnsiConsole.Prompt(
        new TextPrompt<string>($"Password for [cyan1]{user}@{adHocHost}[/]:").Secret());
    var pollMs = int.TryParse(GetArg("--poll-ms"), out var pm) ? pm : 1000;
    var renderMs = int.TryParse(GetArg("--render-ms"), out var rm) ? rm : 500;

    config = new AppConfig
    {
        PollIntervalMs = pollMs,
        RenderIntervalMs = renderMs,
        Servers = new List<ServerConfig>
        {
            new() { Name = name, Host = adHocHost, Port = port, User = user },
        },
    };
    creds = new Dictionary<string, string> { [name] = password };
}
else
{
    var configArgIndex = Array.IndexOf(args, "--config");
    string configPath;

    if (configArgIndex >= 0 && configArgIndex + 1 < args.Length)
    {
        configPath = args[configArgIndex + 1];
        if (!File.Exists(configPath))
        {
            AnsiConsole.MarkupLine($"[red]Config not found: {Markup.Escape(configPath)}[/]");
            return 1;
        }
    }
    else
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var exeConfig = Path.Combine(exeDir, "servers.json");
        var appDataConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SSHRtm", "servers.json");

        if (File.Exists(exeConfig))
        {
            configPath = exeConfig;
        }
        else if (File.Exists(appDataConfig))
        {
            configPath = appDataConfig;
        }
        else
        {
            // No config anywhere — try exe folder first (portable), fall back to %APPDATA%.
            var created = TryCreateSample(exeConfig) ?? TryCreateSample(appDataConfig);
            if (created is null)
            {
                AnsiConsole.MarkupLine("[red]Could not create sample config in exe folder or %APPDATA%.[/]");
                return 1;
            }
            AnsiConsole.MarkupLine($"[yellow]Created sample config at {Markup.Escape(created)}. Edit it and run again.[/]");
            return 0;
        }
    }

    config = ConfigLoader.Load(configPath);
    if (config.Servers.Count == 0)
    {
        AnsiConsole.MarkupLine("[red]No servers defined in config.[/]");
        return 1;
    }

    // Co-locate creds.dat with whichever config we actually loaded — keeps the
    // deployment portable when both live next to the exe.
    var credsDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
    var credStore = new DpapiCredentialStore(credsDir);
    var force = args.Contains("--set-passwords");
    creds = new Dictionary<string, string>();
    foreach (var srv in config.Servers)
    {
        var existing = force ? null : credStore.TryGet(srv.Name);
        if (existing is null)
        {
            var pw = AnsiConsole.Prompt(
                new TextPrompt<string>($"Password for [cyan1]{srv.Name}[/] ({srv.User}):").Secret());
            credStore.Set(srv.Name, pw);
            creds[srv.Name] = pw;
        }
        else
        {
            creds[srv.Name] = existing;
        }
    }

    if (force)
    {
        AnsiConsole.MarkupLine("[green]Passwords stored. Re-run without --set-passwords to launch.[/]");
        return 0;
    }
}

var state = new StateStore(config.Servers.Select(s => s.Name));
var pollerHost = new PollerHost(config, creds, state, cts.Token);
var pollTask = pollerHost.RunAsync();

try
{
    var app = new App(config, state, cts);
    await app.RunAsync();
}
finally
{
    cts.Cancel();
    try { await pollTask; } catch { }
    Console.CursorVisible = true;
}

return 0;

string? GetArg(string flag)
{
    var idx = Array.IndexOf(args, flag);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static string? TryCreateSample(string path)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, ConfigLoader.SampleJson);
        return path;
    }
    catch
    {
        return null;
    }
}

static void PrintHelp()
{
    AnsiConsole.MarkupLine("[bold]SSHRtm[/] - realtime multi-server monitor");
    AnsiConsole.MarkupLine("");
    AnsiConsole.MarkupLine("[bold]Ad-hoc single server (no config file, no DPAPI store):[/]");
    AnsiConsole.MarkupLine("  --host <hostname>       target host (also accepts --server)");
    AnsiConsole.MarkupLine("  --user <username>       ssh username  (required with --host)");
    AnsiConsole.MarkupLine("  --password <pw>         password (prompted if omitted)");
    AnsiConsole.MarkupLine("  --port <n>              ssh port (default 22)");
    AnsiConsole.MarkupLine("  --name <label>         tile label (default: host)");
    AnsiConsole.MarkupLine("  --poll-ms <n>          poll interval ms (default 1000)");
    AnsiConsole.MarkupLine("  --render-ms <n>        render interval ms (default 500)");
    AnsiConsole.MarkupLine("");
    AnsiConsole.MarkupLine("[bold]Config-file mode (multi-server, DPAPI-stored passwords):[/]");
    AnsiConsole.MarkupLine("  --config <path>         use custom config");
    AnsiConsole.MarkupLine("  --set-passwords         (re)enter passwords into the DPAPI store");
    AnsiConsole.MarkupLine("");
    AnsiConsole.MarkupLine("  Config lookup order when --config is not given:");
    AnsiConsole.MarkupLine("    1. <exe-folder>\\servers.json    (portable — creds.dat lives here too)");
    AnsiConsole.MarkupLine("    2. %APPDATA%\\SSHRtm\\servers.json");
    AnsiConsole.MarkupLine("  creds.dat is always co-located with the loaded servers.json.");
    AnsiConsole.MarkupLine("");
    AnsiConsole.MarkupLine("  --help / -h             show this");
    AnsiConsole.MarkupLine("");
    AnsiConsole.MarkupLine("[grey58]Note: passwords on the command line are visible via Get-Process and shell history.[/]");
    AnsiConsole.MarkupLine("[grey58]Prefer omitting --password to be prompted, or use the config-file + DPAPI flow.[/]");
}
