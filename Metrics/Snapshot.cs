namespace SSHRtm.Metrics;

public sealed record Snapshot(
    string Hostname,
    int NumCpu,
    string[] LoadAvg,
    double UptimeSec,
    Dictionary<string, double> CpuPct,
    MemoryStats Memory,
    Dictionary<string, NetRate> NetRate,
    Dictionary<string, DiskRate> DiskRate,
    List<FilesystemInfo> Filesystems,
    List<ProcessInfo> TopProcs,
    RawCounters Raw);

public sealed record MemoryStats(
    ulong Total, ulong Available, ulong Used, double UsedPct,
    ulong Cached, ulong Buffers,
    ulong SwapTotal, ulong SwapUsed, double SwapPct);

public sealed record NetRate(double RxBps, double TxBps, double RxPps, double TxPps);

public sealed record DiskRate(double ReadBps, double WriteBps, double BusyPct, ulong InFlight);

public sealed record FilesystemInfo(
    string Filesystem, string Type,
    ulong TotalKb, ulong UsedKb, ulong AvailKb,
    int UsedPct, string Mount);

public sealed record ProcessInfo(int Pid, double Cpu, double Mem, string Command);

public sealed record RawCounters(
    Dictionary<string, CpuCounters> Cpu,
    Dictionary<string, NetCounters> Net,
    Dictionary<string, DiskCounters> Disk);

public sealed record CpuCounters(ulong Idle, ulong Total);

public sealed record NetCounters(
    ulong RxBytes, ulong RxPackets, ulong RxErrs, ulong RxDrop,
    ulong TxBytes, ulong TxPackets, ulong TxErrs, ulong TxDrop);

public sealed record DiskCounters(
    ulong ReadsCompleted, ulong SectorsRead,
    ulong WritesCompleted, ulong SectorsWritten,
    ulong IoInFlight, ulong IoTimeMs);
