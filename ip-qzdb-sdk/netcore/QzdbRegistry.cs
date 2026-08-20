namespace QQZeng.Qzdb;

using System.Collections.Concurrent;

public sealed class QzdbRegistry
{
    private static readonly QzdbRegistry GlobalInstance = new();
    private readonly ConcurrentDictionary<string, QzdbReader> _map = new();
    private readonly object _gate = new();

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
        old?.Dispose();
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
        old?.Dispose();
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
        old?.Dispose();
    }

    public void Clear()
    {
        QzdbReader[] readers;
        lock (_gate)
        {
            readers = _map.Values.ToArray();
            _map.Clear();
        }
        foreach (var reader in readers) reader.Dispose();
    }

    public static void RegisterGlobal(string name, string path, ReaderOptions? options = null) => GlobalInstance.Register(name, path, options);
    public static void RegisterGlobalBuffer(string name, byte[] buffer, ReaderOptions? options = null) => GlobalInstance.RegisterBuffer(name, buffer, options);
    public static QzdbReader? GetGlobal(string name) => GlobalInstance.Get(name);
    public static GeoInfo? FindGlobal(string name, string ipStr) => GlobalInstance.Find(name, ipStr);
    public static string FindStrGlobal(string name, string ipStr) => GlobalInstance.FindStr(name, ipStr);
    public static void UnregisterGlobal(string name) => GlobalInstance.Unregister(name);
    public static void ClearGlobal() => GlobalInstance.Clear();
}
