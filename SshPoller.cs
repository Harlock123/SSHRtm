using Renci.SshNet;

namespace SSHRtm;

public sealed class SshPoller : IDisposable
{
    public const string PollCommand = """
        cat /proc/stat        ; echo '==SEP:STAT=='
        cat /proc/meminfo     ; echo '==SEP:MEMINFO=='
        cat /proc/loadavg     ; echo '==SEP:LOADAVG=='
        cat /proc/uptime      ; echo '==SEP:UPTIME=='
        cat /proc/net/dev     ; echo '==SEP:NETDEV=='
        cat /proc/diskstats   ; echo '==SEP:DISKSTATS=='
        df -PT -x tmpfs -x devtmpfs -x overlay 2>/dev/null ; echo '==SEP:DF=='
        ps -eo pid,pcpu,pmem,comm --sort=-pcpu --no-headers 2>/dev/null | head -20 ; echo '==SEP:PS=='
        nproc ; echo '==SEP:NPROC=='
        hostname ; echo '==SEP:END=='
        """;

    private readonly ServerConfig _config;
    private readonly string _password;
    private SshClient? _client;

    public SshPoller(ServerConfig config, string password)
    {
        _config = config;
        _password = password;
    }

    private SshClient CreateClient()
    {
        var pwAuth = new PasswordAuthenticationMethod(_config.User, _password);
        var kbAuth = new KeyboardInteractiveAuthenticationMethod(_config.User);
        kbAuth.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                if (prompt.Request.Contains("password", StringComparison.OrdinalIgnoreCase))
                    prompt.Response = _password;
            }
        };
        var connInfo = new ConnectionInfo(
            _config.Host, _config.Port, _config.User, pwAuth, kbAuth)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        var client = new SshClient(connInfo);
        client.HostKeyReceived += (_, e) => e.CanTrust = true;
        return client;
    }

    public void Connect()
    {
        _client?.Dispose();
        _client = CreateClient();
        _client.Connect();
    }

    public string Poll()
    {
        if (_client is null || !_client.IsConnected) Connect();
        using var cmd = _client!.CreateCommand(PollCommand);
        cmd.CommandTimeout = TimeSpan.FromSeconds(15);
        var result = cmd.Execute();
        if (cmd.ExitStatus != 0)
        {
            throw new InvalidOperationException(
                $"Remote exit {cmd.ExitStatus}: {cmd.Error}");
        }
        return result;
    }

    public void Dispose()
    {
        try { _client?.Disconnect(); } catch { }
        _client?.Dispose();
    }
}
