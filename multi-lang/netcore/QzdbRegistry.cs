namespace Qzdb;

using System.Collections.Concurrent;

public sealed class QzdbRegistry
{
    private static readonly QzdbRegistry GlobalInstance = new();
    private readonly ConcurrentDictionary<string, DatabaseReader> _map = new();

    public void Register(string name, string path)
    {
        var reader = new DatabaseReader.Builder(path).Build();
        var old = _map.AddOrUpdate(name, reader, (_, _) => reader);
        old?.Dispose();
    }

    public DatabaseReader? Get(string name) => _map.GetValueOrDefault(name);

    public void Unregister(string name)
    {
        if (_map.TryRemove(name, out var old)) old.Dispose();
    }

    public static void RegisterGlobal(string name, string path) => GlobalInstance.Register(name, path);
    public static DatabaseReader? GetGlobal(string name) => GlobalInstance.Get(name);
    public static void UnregisterGlobal(string name) => GlobalInstance.Unregister(name);
}
