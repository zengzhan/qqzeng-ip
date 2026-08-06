namespace Qzdb;

using System.Collections.Generic;

public sealed class ChainedReader
{
    public enum Mode { Fallback, Merge, MergeOverride }

    private readonly DatabaseReader[] _readers;
    private readonly Mode _mode;

    private ChainedReader(DatabaseReader[] readers, Mode mode)
    {
        _readers = readers;
        _mode = mode;
    }

    public static ChainedReader Chain(params DatabaseReader[] readers) => new(readers, Mode.Fallback);
    public static ChainedReader ChainMerge(params DatabaseReader[] readers) => new(readers, Mode.Merge);
    public static ChainedReader ChainMergeOverride(params DatabaseReader[] readers) => new(readers, Mode.MergeOverride);

    public GeoInfo? Find(string ipStr)
    {
        if (_mode == Mode.Fallback)
        {
            foreach (var r in _readers)
            {
                try { var res = r.Find(ipStr); if (res != null) return res; }
                catch (QzdbException e) { if (e.ErrorCode == ErrorCode.InvalidIp) throw; }
            }
            return null;
        }
        else
        {
            var merged = new Dictionary<string, string>();
            foreach (var r in _readers)
            {
                try
                {
                    var info = r.Find(ipStr);
                    if (info != null)
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
                }
                catch (QzdbException e) { if (e.ErrorCode == ErrorCode.InvalidIp) throw; }
            }
            if (merged.Count == 0) return null;
            var names = merged.Keys.ToArray();
            var values = names.Select(n => merged[n]).ToArray();
            return new GeoInfo(names, values, GeoInfo.BuildNormalizedMap(names), null);
        }
    }

    public string[] Editions() => _readers.Select(r => r.Edition).ToArray();
    public string[] Scopes() => _readers.Select(r => r.Scope).ToArray();
    public string[] DataMonths() => _readers.Select(r => r.DataMonth).ToArray();
    public IReadOnlyList<DatabaseReader> Readers => _readers;
}
