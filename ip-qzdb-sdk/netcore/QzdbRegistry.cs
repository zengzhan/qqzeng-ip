namespace QQZeng.Qzdb;

using System.Collections.Concurrent;

public sealed class QzdbRegistry
{
    private static readonly QzdbRegistry GlobalInstance = new();
    private readonly ConcurrentDictionary<string, QzdbReader> _map = new();
    private readonly object _gate = new();

    // ------------------------------------------------------------------
    // Retirement quarantine.
    //
    // BUG THIS FIXES: Get(name) hands out the QzdbReader instance itself.
    // A caller can legitimately do:
    //     var r = registry.Get("geo"); ... r.Find(ip) ...
    // with a Register("geo", newPath) racing on another thread in between —
    // exactly the hot-swap use case this class exists for. The previous
    // implementation called old.Dispose() synchronously the moment the new
    // reader was published, so that caller's Find() could throw
    // ObjectDisposedException (or worse, race the mmap unmap inside
    // QzdbReader.Dispose) purely because of scheduling luck. A registry
    // whose entire purpose is safe hot-reload must not do that.
    //
    // FIX: same reasoning as QzdbReader's own mmap quarantine (see
    // QzdbReader._retiring) applied one layer up — Register/Unregister/Clear
    // are rare, operator-triggered events (minutes-to-hours apart), while any
    // in-flight Find() call is a microsecond-scale critical section. Holding
    // a small bounded number of just-retired readers alive for a few more
    // mutations, instead of disposing them the instant they're replaced,
    // gives in-flight callers time to finish without needing per-call
    // reference counting. QuarantineCapacity bounds the worst case to a
    // handful of extra open readers, never unbounded growth.
    // ------------------------------------------------------------------
    private const int QuarantineCapacity = 8;
    private readonly ConcurrentQueue<QzdbReader> _quarantine = new();

    public static QzdbRegistry Default => GlobalInstance;

    public void Register(string name, string path, ReaderOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var reader = QzdbReader.Open(path, options);
        QzdbReader? old;
        lock (_gate)
        {
            _map.TryGetValue(name, out old);
            _map[name] = reader;
        }
        Retire(old);
    }

    public void RegisterBuffer(string name, byte[] buffer, ReaderOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var reader = QzdbReader.OpenBuffer(buffer, options);
        QzdbReader? old;
        lock (_gate)
        {
            _map.TryGetValue(name, out old);
            _map[name] = reader;
        }
        Retire(old);
    }

    /// <summary>
    /// Move a replaced/removed reader into the bounded quarantine instead of
    /// disposing it immediately, so any caller still holding a reference from
    /// a prior Get() has time to finish. Evicts (and disposes) the oldest
    /// quarantined reader once the queue exceeds <see cref="QuarantineCapacity"/>.
    /// </summary>
    private void Retire(QzdbReader? old)
    {
        if (old == null) return;
        _quarantine.Enqueue(old);
        while (_quarantine.Count > QuarantineCapacity && _quarantine.TryDequeue(out var evicted))
            evicted.Dispose();
    }

    public QzdbReader? Get(string name) => string.IsNullOrEmpty(name) ? null : _map.GetValueOrDefault(name);

    public GeoInfo? Find(string name, string ipStr) => Get(name)?.Find(ipStr);
    public GeoInfo? Find(string name, ReadOnlySpan<char> ipSpan) => Get(name)?.Find(ipSpan);
    public GeoInfo? Find(string name, System.Net.IPAddress address) => Get(name)?.Find(address);
    public GeoInfo? Find(string name, ReadOnlySpan<byte> ipBytes) => Get(name)?.Find(ipBytes);

    public string FindStr(string name, string ipStr) => Get(name)?.FindStr(ipStr) ?? string.Empty;
    public string FindStr(string name, ReadOnlySpan<char> ipSpan) => Get(name)?.FindStr(ipSpan) ?? string.Empty;
    public string FindStr(string name, ReadOnlySpan<byte> ipBytes) => Get(name)?.FindStr(ipBytes) ?? string.Empty;

    public uint LookupRowId(string name, string ipStr) => Get(name)?.LookupRowId(ipStr) ?? 0;
    public uint LookupRowId(string name, ReadOnlySpan<char> ipSpan) => Get(name)?.LookupRowId(ipSpan) ?? 0;
    public uint LookupRowId(string name, ReadOnlySpan<byte> ipBytes) => Get(name)?.LookupRowId(ipBytes) ?? 0;

    public void Unregister(string name)
    {
        QzdbReader? old = null;
        lock (_gate) { _map.TryRemove(name, out old); }
        Retire(old);
    }

    /// <summary>
    /// Removes every registered reader. Unlike Register/Unregister, Clear() is
    /// treated as a terminal shutdown action (analogous to QzdbReader.Dispose)
    /// rather than a hot-swap, so readers are disposed immediately rather than
    /// quarantined — callers invoking Clear() are expected to have already
    /// stopped issuing queries, same contract as disposing any reader directly.
    /// Anything still sitting in the hot-swap quarantine from earlier
    /// Register/Unregister calls is flushed at the same time.
    /// </summary>
    public void Clear()
    {
        QzdbReader[] readers;
        lock (_gate)
        {
            readers = _map.Values.ToArray();
            _map.Clear();
        }
        foreach (var reader in readers) reader.Dispose();
        while (_quarantine.TryDequeue(out var q)) q.Dispose();
    }

    public static void RegisterGlobal(string name, string path, ReaderOptions? options = null) => GlobalInstance.Register(name, path, options);
    public static void RegisterGlobalBuffer(string name, byte[] buffer, ReaderOptions? options = null) => GlobalInstance.RegisterBuffer(name, buffer, options);
    public static QzdbReader? GetGlobal(string name) => GlobalInstance.Get(name);
    public static GeoInfo? FindGlobal(string name, string ipStr) => GlobalInstance.Find(name, ipStr);
    public static string FindStrGlobal(string name, string ipStr) => GlobalInstance.FindStr(name, ipStr);
    public static void UnregisterGlobal(string name) => GlobalInstance.Unregister(name);
    public static void ClearGlobal() => GlobalInstance.Clear();
}
