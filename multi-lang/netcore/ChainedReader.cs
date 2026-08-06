namespace QQZeng.Qzdb;

using System.Collections.Generic;

public sealed class ChainedReader : IDisposable
{
    public enum Mode { Fallback, Merge, MergeOverride }

    private readonly QzdbReader[] _readers;
    private readonly Mode _mode;

    private ChainedReader(QzdbReader[] readers, Mode mode)
    {
        _readers = readers;
        _mode = mode;
    }

    public static ChainedReader Chain(params QzdbReader[] readers) => new(readers, Mode.Fallback);
    public static ChainedReader ChainMerge(params QzdbReader[] readers) => new(readers, Mode.Merge);
    public static ChainedReader ChainMergeOverride(params QzdbReader[] readers) => new(readers, Mode.MergeOverride);

    public GeoInfo? Find(string ipStr) => ChainQuery(r => r.Find(ipStr));

    public GeoInfo? FindUint(uint ipInt) => ChainQuery(r => r.FindUint(ipInt));

    public GeoInfo? FindBytes(byte[]? ipBytes) => ChainQuery(r => r.FindBytes(ipBytes));

    public GeoInfo? FindFields(string ipStr, string[]? fields) => ChainQuery(r => r.FindFields(ipStr, fields));

    public BatchResult[] FindBatch(IEnumerable<string> ipStrs)
    {
        if (ipStrs == null) return Array.Empty<BatchResult>();
        return ipStrs.Select(FindResult).ToArray();
    }

    public BatchResult[] FindBatchFields(IEnumerable<string> ipStrs, IEnumerable<string>? fields)
    {
        if (ipStrs == null) return Array.Empty<BatchResult>();
        var flds = fields?.ToArray();
        return ipStrs.Select(ip => LookupResult(ip, flds)).ToArray();
    }

    public IEnumerable<BatchResult> FindStream(IEnumerable<string> ipStrs)
    {
        if (ipStrs == null) yield break;
        foreach (var ip in ipStrs) yield return FindResult(ip);
    }

    private BatchResult FindResult(string ip)
    {
        try { return new BatchResult(Find(ip), null); }
        catch (QzdbException e) { return new BatchResult(null, e); }
    }

    private BatchResult LookupResult(string ip, string[]? fields)
    {
        try { return new BatchResult(FindFields(ip, fields), null); }
        catch (QzdbException e) { return new BatchResult(null, e); }
    }

    private GeoInfo? ChainQuery(Func<QzdbReader, GeoInfo?> query)
    {
        if (_mode == Mode.Fallback)
        {
            foreach (var r in _readers)
            {
                try { var res = query(r); if (res != null) return res; }
                catch (QzdbException e) { if (e.ErrorCode == ErrorCode.InvalidIp) throw; }
            }
            return null;
        }

        var merged = new Dictionary<string, string>();
        foreach (var r in _readers)
        {
            try
            {
                var info = query(r);
                if (info != null) MergeInfo(merged, info);
            }
            catch (QzdbException e) { if (e.ErrorCode == ErrorCode.InvalidIp) throw; }
        }
        if (merged.Count == 0) return null;
        var names = merged.Keys.ToArray();
        var values = names.Select(n => merged[n]).ToArray();
        return new GeoInfo(names, values, GeoInfo.BuildNormalizedMap(names), null);
    }

    private void MergeInfo(Dictionary<string, string> merged, GeoInfo info)
    {
        var fields = info.FieldNames;
        var vals = info.Values;
        for (int i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            var v = i < vals.Length && vals[i] != null ? vals[i] : "";
            if (_mode == Mode.Merge)
            {
                if (!merged.ContainsKey(f)) merged[f] = v;
                else if (string.IsNullOrEmpty(merged[f]) && !string.IsNullOrEmpty(v)) merged[f] = v;
            }
            else
            {
                if (!string.IsNullOrEmpty(v) || !merged.ContainsKey(f)) merged[f] = v;
            }
        }
    }

    public string[] Editions() => _readers.Select(r => r.Edition).ToArray();
    public string[] Scopes() => _readers.Select(r => r.Scope).ToArray();
    public string[] DataMonths() => _readers.Select(r => r.DataMonth).ToArray();
    public IReadOnlyList<QzdbReader> Readers => _readers;

    /// <summary>Releases only the aggregation state; does NOT close the underlying readers (per API spec §9.4).</summary>
    public void Dispose()
    {
        foreach (var r in _readers) GC.KeepAlive(r);
    }
}
