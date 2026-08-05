using System.Collections.Concurrent;
using SSHRtm.Metrics;

namespace SSHRtm;

public enum ServerStatus { Connecting, Ok, Error }

public sealed record ServerState(
    string Name,
    ServerStatus Status,
    string? LastError,
    Snapshot? Snapshot,
    DateTime UpdatedAt);

public sealed class StateStore
{
    private readonly ConcurrentDictionary<string, ServerState> _map = new();

    public StateStore(IEnumerable<string> names)
    {
        foreach (var n in names)
        {
            _map[n] = new ServerState(n, ServerStatus.Connecting, null, null, DateTime.UtcNow);
        }
    }

    public ServerState Get(string name) => _map[name];
    public void Set(string name, ServerState s) => _map[name] = s;
}
