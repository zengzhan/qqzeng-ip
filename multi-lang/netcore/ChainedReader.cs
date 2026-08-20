namespace QQZeng.Qzdb;

using System.Collections.Generic;

public sealed class ChainedReader : IDisposable
{
    public enum Mode { Fallback, Merge, MergeOverride }

    private readonly IReadOnlyList<QzdbReader> _readers;
    private readonly Mode _mode;

    private ChainedReader(QzdbReader[] readers, Mode mode)
    {
        ArgumentNullException.ThrowIfNull(readers);
        if (readers.Any(r => r == null)) throw new ArgumentException("Readers cannot contain null", nameof(readers));
        _readers = Array.AsReadOnly((QzdbReader[])readers.Clone());
        _mode = mode;
    }

    public static ChainedReader Chain(params QzdbReader[] readers) => new(readers, Mode.Fallback);
    public static ChainedReader ChainMerge(params QzdbReader[] readers) => new(readers, Mode.Merge);
    public static ChainedReader ChainMergeOverride(params QzdbReader[] readers) => new(readers, Mode.MergeOverride);

    public GeoInfo? Find(string ipStr) => Find(ipStr.AsSpan());

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

    public GeoInfo? FindBytes(byte[]? ipBytes)
    {
        if (ipBytes == null) throw new QzdbException(ErrorCode.InvalidIp, "IP bytes cannot be null");
        return Find(ipBytes.AsSpan());
    }

    public GeoInfo? FindFields(string ipStr, string[]? fields) => FindFields(ipStr.AsSpan(), fields);

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

    public BatchResult[] FindBatch(string[] ipStrs)
    {
        ArgumentNullException.ThrowIfNull(ipStrs);
        var result = new BatchResult[ipStrs.Length];
        for (int i = 0; i < ipStrs.Length; i++) result[i] = FindResult(ipStrs[i]);
        return result;
    }

    public BatchResult[] FindBatch(IEnumerable<string> ipStrs) => FindBatch(ipStrs?.ToArray() ?? throw new ArgumentNullException(nameof(ipStrs)));

    public BatchResult[] FindBatchFields(string[] ipStrs, string[]? fields)
    {
        ArgumentNullException.ThrowIfNull(ipStrs);
        var result = new BatchResult[ipStrs.Length];
        for (int i = 0; i < ipStrs.Length; i++) result[i] = LookupResult(ipStrs[i], fields);
        return result;
    }

    public BatchResult[] FindBatchFields(IEnumerable<string> ipStrs, IEnumerable<string>? fields) =>
        FindBatchFields(ipStrs?.ToArray() ?? throw new ArgumentNullException(nameof(ipStrs)), fields?.ToArray());

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

    public string[] Editions => _readers.Select(r => r.Edition).ToArray();
    public string[] Scopes => _readers.Select(r => r.Scope).ToArray();
    public string[] DataMonths => _readers.Select(r => r.DataMonth).ToArray();
    public QzdbReader[] Readers => _readers.ToArray();

    public string[] GetEditions() => Editions;
    public string[] GetScopes() => Scopes;
    public string[] GetDataMonths() => DataMonths;

    /// <summary>Releases only the aggregation state; does NOT close the underlying readers (per API spec §9.4).</summary>
    public void Dispose()
    {
        foreach (var r in _readers) GC.KeepAlive(r);
    }
}
