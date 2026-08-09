// 元信息探针（C#）：输出与 tools/meta_probe_python.py 完全同构的 JSON。
//
// 用法（临时项目引用 multi-lang/netcore/QQZeng.Qzdb.csproj）：
//   dotnet run --project /tmp/csprobe -c Release -- a.qzdb b.qzdb ... > /tmp/meta_cs.json
using System.Text;
using System.Text.Json;
using QQZeng.Qzdb;

var opts = new JsonWriterOptions { Indented = false };
using var ms = new MemoryStream();
using (var w = new Utf8JsonWriter(ms, opts))
{
    w.WriteStartArray();
    foreach (var path in args)
    {
        using var r = QzdbReader.Open(path);
        w.WriteStartObject();
        w.WriteString("file", Path.GetFileName(path));
        w.WriteString("lang", "csharp");
        w.WriteString("edition", r.Edition);
        w.WriteString("edition_source", r.EditionSource);
        w.WriteNumber("version_mask", r.VersionMask);
        w.WriteString("field_names_source", r.FieldNamesSource);
        w.WriteStartArray("field_names");
        foreach (var n in r.FieldNames) w.WriteStringValue(n);
        w.WriteEndArray();
        w.WriteNumber("group_count", r.GroupCount);
        w.WriteNumber("pool_count", r.PoolCount);
        w.WriteString("data_month", r.DataMonth);
        w.WriteEndObject();
    }
    w.WriteEndArray();
}
Console.Out.Write(Encoding.UTF8.GetString(ms.ToArray()));
