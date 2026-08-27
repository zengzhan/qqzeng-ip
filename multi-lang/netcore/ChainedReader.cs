namespace QQZeng.Qzdb;

using System.Collections.Generic;
using System.Collections.ObjectModel;

/// <summary>Combines multiple <see cref="QzdbReader"/> instances into one logical query surface, with Fallback / Merge / MergeOverride strategies.</summary>
public sealed class ChainedReader : IDisposable
{
    /// <summary>Strategy used to combine results from the chained readers.</summary>
    public enum Mode
    {
        /// <summary>Returns the first reader's hit, in order; later readers are consulted only on a miss.</summary>
        Fallback,
        /// <summary>Merges all hits; a later reader fills only fields left empty by earlier readers (earlier wins).</summary>
        Merge,
        /// <summary>Merges all hits; a later reader's value overrides an earlier reader's value (later wins).</summary>
        MergeOverride
    }

    private readonly ReadOnlyCollection<QzdbReader> _readers;
    private readonly Mode _mode;

    private ChainedReader(QzdbReader[] readers, Mode mode)
    {
        ArgumentNullException.ThrowIfNull(readers);
        if (readers.Any(r => r == null)) throw new ArgumentException("Readers cannot contain null", nameof(readers));
        _readers = Array.AsReadOnly((QzdbReader[])readers.Clone());
        _mode = mode;
    }

    /// <summary>Creates a Fallback-mode chained reader over the given readers.</summary>
    public static ChainedReader Chain(params QzdbReader[] readers) => new(readers, Mode.Fallback);
    /// <summary>Creates a Merge-mode chained reader over the given readers.</summary>
    public static ChainedReader ChainMerge(params QzdbReader[] readers) => new(readers, Mode.Merge);
    /// <summary>Creates a MergeOverride-mode chained reader over the given readers.</summary>
    public static ChainedReader ChainMergeOverride(params QzdbReader[] readers) => new(readers, Mode.MergeOverride);

    /// <summary>Queries the chain for an IP string (per-mode semantics; see <see cref="QzdbReader.Find(string)"/>).</summary>
    public GeoInfo? Find(string ipStr) => Find(ipStr.AsSpan());

    /// <summary>Queries the chain for an IP character span (per-mode semantics).</summary>
    public GeoInfo? Find(ReadOnlySpan<char> ipSpan)
    {
        if (_mode == Mode.Fallback)
        {
            for (int i = 0; i < _readers.Count; i++)
            {
                var res = _readers[i].Find(ipSpan);
                if (res != null) return res;
            }
            return null;
        }

        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        var values = new List<string>();
        for (int i = 0; i < _readers.Count; i++)
        {
            var info = _readers[i].Find(ipSpan);
            if (info != null) MergeInfo(indexes, names, values, info);
        }
        if (names.Count == 0) return null;
        var nameArray = names.ToArray();
        return new GeoInfo(nameArray, values.ToArray(), GeoInfo.BuildNormalizedMap(nameArray), null, takeOwnership: true);
    }

    /// <summary>Queries the chain for a System.Net.IPAddress (per-mode semantics).</summary>
    public GeoInfo? Find(System.Net.IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        Span<byte> bytes = stackalloc byte[16];
        if (address.TryWriteBytes(bytes, out int written))
        {
            return Find(bytes[..written]);
        }
        return null;
    }

    /// <summary>Queries the chain for raw IP bytes (4 or 16); per-mode semantics.</summary>
    public GeoInfo? Find(ReadOnlySpan<byte> ipBytes)
    {
        if (_mode == Mode.Fallback)
        {
            for (int i = 0; i < _readers.Count; i++)
            {
                var res = _readers[i].Find(ipBytes);
                if (res != null) return res;
            }
            return null;
        }

        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        var values = new List<string>();
        for (int i = 0; i < _readers.Count; i++)
        {
            var info = _readers[i].Find(ipBytes);
            if (info != null) MergeInfo(indexes, names, values, info);
        }
        if (names.Count == 0) return null;
        var nameArray = names.ToArray();
        return new GeoInfo(nameArray, values.ToArray(), GeoInfo.BuildNormalizedMap(nameArray), null, takeOwnership: true);
    }

    /// <summary>Queries the chain for an IPv4 uint (per-mode semantics).</summary>
    public GeoInfo? FindUint(uint ipInt)
    {
        if (_mode == Mode.Fallback)
        {
            for (int i = 0; i < _readers.Count; i++)
            {
                var res = _readers[i].FindUint(ipInt);
                if (res != null) return res;
            }
            return null;
        }

        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        var values = new List<string>();
        for (int i = 0; i < _readers.Count; i++)
        {
            var info = _readers[i].FindUint(ipInt);
            if (info != null) MergeInfo(indexes, names, values, info);
        }
        if (names.Count == 0) return null;
        var nameArray = names.ToArray();
        return new GeoInfo(nameArray, values.ToArray(), GeoInfo.BuildNormalizedMap(nameArray), null, takeOwnership: true);
    }

    /// <summary>Queries the chain for IP bytes; a null argument throws QzdbException(InvalidIp).</summary>
    public GeoInfo? FindBytes(byte[]? ipBytes)
    {
        if (ipBytes == null) throw new QzdbException(ErrorCode.InvalidIp, "IP bytes cannot be null");
        return Find(ipBytes.AsSpan());
    }

    /// <summary>Queries the chain for selected fields of an IP string (per-mode semantics).</summary>
    public GeoInfo? FindFields(string ipStr, string[]? fields) => FindFields(ipStr.AsSpan(), fields);

    /// <summary>Queries the chain for selected fields of an IP span (per-mode semantics).</summary>
    public GeoInfo? FindFields(ReadOnlySpan<char> ipSpan, string[]? fields)
    {
        if (_mode == Mode.Fallback)
        {
            for (int i = 0; i < _readers.Count; i++)
            {
                var res = _readers[i].FindFields(ipSpan, fields);
                if (res != null) return res;
            }
            return null;
        }

        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        var values = new List<string>();
        for (int i = 0; i < _readers.Count; i++)
        {
            var info = _readers[i].FindFields(ipSpan, fields);
            if (info != null) MergeInfo(indexes, names, values, info);
        }
        if (names.Count == 0) return null;
        var nameArray = names.ToArray();
        return new GeoInfo(nameArray, values.ToArray(), GeoInfo.BuildNormalizedMap(nameArray), null, takeOwnership: true);
    }

    /// <summary>Batch query over IP strings; each result carries its three-state semantics.</summary>
    public BatchResult[] FindBatch(string[] ipStrs)
    {
        ArgumentNullException.ThrowIfNull(ipStrs);
        var result = new BatchResult[ipStrs.Length];
        for (int i = 0; i < ipStrs.Length; i++) result[i] = FindResult(ipStrs[i]);
        return result;
    }

    /// <summary>Batch query over an enumerable of IP strings.</summary>
    public BatchResult[] FindBatch(IEnumerable<string> ipStrs) => FindBatch(ipStrs?.ToArray() ?? throw new ArgumentNullException(nameof(ipStrs)));

    /// <summary>Batch query over IP strings, resolving only the given fields.</summary>
    public BatchResult[] FindBatchFields(string[] ipStrs, string[]? fields)
    {
        ArgumentNullException.ThrowIfNull(ipStrs);
        var result = new BatchResult[ipStrs.Length];
        for (int i = 0; i < ipStrs.Length; i++) result[i] = LookupResult(ipStrs[i], fields);
        return result;
    }

    /// <summary>Batch query over enumerables of IP strings and fields.</summary>
    public BatchResult[] FindBatchFields(IEnumerable<string> ipStrs, IEnumerable<string>? fields) =>
        FindBatchFields(ipStrs?.ToArray() ?? throw new ArgumentNullException(nameof(ipStrs)), fields?.ToArray());

    /// <summary>Lazily streams batch results for an enumerable of IP strings.</summary>
    public IEnumerable<BatchResult> FindStream(IEnumerable<string> ipStrs)
    {
        if (ipStrs == null) yield break;
        foreach (var ip in ipStrs) yield return FindResult(ip);
    }

    private BatchResult FindResult(string ip)
    {
        try { return new BatchResult(Find(ip), null, ip); }
        catch (QzdbException e) { return new BatchResult(null, e, ip); }
    }

    private BatchResult LookupResult(string ip, string[]? fields)
    {
        try { return new BatchResult(FindFields(ip, fields), null, ip); }
        catch (QzdbException e) { return new BatchResult(null, e, ip); }
    }



    private void MergeInfo(Dictionary<string, int> indexes, List<string> names, List<string> values, GeoInfo info)
    {
        var fields = info.FieldNames;
        var vals = info.Values;
        for (int i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            var v = i < vals.Length && vals[i] != null ? vals[i] : "";
            var key = GeoInfo.NormalizeKey(f);
            if (!indexes.TryGetValue(key, out var index))
            {
                indexes[key] = names.Count;
                names.Add(f);
                values.Add(v);
            }
            else if (_mode == Mode.Merge && string.IsNullOrEmpty(values[index]) && !string.IsNullOrEmpty(v))
            {
                values[index] = v;
            }
            else if (_mode == Mode.MergeOverride)
                values[index] = v;
        }
    }

    /// <summary>Editions of the underlying readers, in chain order.</summary>
    public string[] Editions => _readers.Select(r => r.Edition).ToArray();
    /// <summary>Scopes of the underlying readers, in chain order.</summary>
    public string[] Scopes => _readers.Select(r => r.Scope).ToArray();
    /// <summary>Data months of the underlying readers, in chain order.</summary>
    public string[] DataMonths => _readers.Select(r => r.DataMonth).ToArray();
    /// <summary>Defensive copy of the underlying readers, in chain order.</summary>
    public QzdbReader[] Readers => _readers.ToArray();

    /// <summary>Returns <see cref="Editions"/>.</summary>
    public string[] GetEditions() => Editions;
    /// <summary>Returns <see cref="Scopes"/>.</summary>
    public string[] GetScopes() => Scopes;
    /// <summary>Returns <see cref="DataMonths"/>.</summary>
    public string[] GetDataMonths() => DataMonths;

    /// <summary>Releases only the aggregation state; does NOT close the underlying readers (per API spec §9.4).</summary>
    public void Dispose()
    {
        foreach (var r in _readers) GC.KeepAlive(r);
    }
}
