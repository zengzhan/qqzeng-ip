namespace QQZeng.Qzdb;

using System.Collections.Concurrent;

public sealed class QzdbRegistry
{
    private static readonly QzdbRegistry GlobalInstance = new();
    private readonly ConcurrentDictionary<string, QzdbReader> _map = new();

    public void Register(string name, string path)
    {
        var reader = new QzdbReader.Builder(path).Build();
        var existing = _map.AddOrUpdate(name, reader, (_, _) => reader);
        if (!ReferenceEquals(existing, reader))
            existing.Dispose();
    }

    public QzdbReader? Get(string name) => _map.GetValueOrDefault(name);

    public void Unregister(string name)
    {
        if (_map.TryRemove(name, out var old)) old.Dispose();
    }

    public void Clear()
    {
        foreach (var kvp in _map)
        {
            if (_map.TryRemove(kvp.Key, out var r)) r.Dispose();
        }
    }

    public static void RegisterGlobal(string name, string path) => GlobalInstance.Register(name, path);
    public static QzdbReader? GetGlobal(string name) => GlobalInstance.Get(name);
    public static void UnregisterGlobal(string name) => GlobalInstance.Unregister(name);
    public static void ClearGlobal() => GlobalInstance.Clear();
}
