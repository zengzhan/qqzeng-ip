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
    public static void UnregisterGlobal(string name) => GlobalInstance.Unregister(name);
    public static void ClearGlobal() => GlobalInstance.Clear();
}
