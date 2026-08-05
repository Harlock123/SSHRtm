using SSHRtm.Metrics;

namespace SSHRtm;

public sealed class PollerHost
{
    private readonly AppConfig _config;
    private readonly Dictionary<string, string> _creds;
    private readonly StateStore _state;
    private readonly CancellationToken _token;

    public PollerHost(AppConfig config, Dictionary<string, string> creds, StateStore state, CancellationToken token)
    {
        _config = config;
        _creds = creds;
        _state = state;
        _token = token;
    }

    public Task RunAsync()
    {
        var tasks = _config.Servers.Select(RunServerAsync).ToArray();
        return Task.WhenAll(tasks);
    }

    private async Task RunServerAsync(ServerConfig srv)
    {
        SshPoller? poller = null;
        RawCounters? prevRaw = null;
        DateTime? prevTime = null;

        while (!_token.IsCancellationRequested)
        {
            try
            {
                if (poller is null)
                {
                    _state.Set(srv.Name, new ServerState(
                        srv.Name, ServerStatus.Connecting, null,
                        _state.Get(srv.Name).Snapshot, DateTime.UtcNow));
                    poller = new SshPoller(srv, _creds[srv.Name]);
                    poller.Connect();
                    prevRaw = null;
                    prevTime = null;
                }

                var raw = await Task.Run(() => poller!.Poll(), _token);
                var now = DateTime.UtcNow;
                var delta = prevTime.HasValue ? (now - prevTime.Value).TotalSeconds : 1.0;
                if (delta <= 0) delta = 1.0;

                var snap = ProcParser.BuildSnapshot(raw, prevRaw, delta);
                _state.Set(srv.Name, new ServerState(
                    srv.Name, ServerStatus.Ok, null, snap, now));
                prevRaw = snap.Raw;
                prevTime = now;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _state.Set(srv.Name, new ServerState(
                    srv.Name, ServerStatus.Error, ex.Message,
                    _state.Get(srv.Name).Snapshot, DateTime.UtcNow));
                try { poller?.Dispose(); } catch { }
                poller = null;
                try { await Task.Delay(3000, _token); } catch { break; }
            }

            try { await Task.Delay(_config.PollIntervalMs, _token); }
            catch { break; }
        }

        try { poller?.Dispose(); } catch { }
    }
}
