/*
 * 元信息探针（Java）：输出与 tools/meta_probe_python.py 完全同构的 JSON。
 *
 * 编译:
 *   javac -encoding UTF-8 -d /tmp/javabuild $(find multi-lang/java/src/main/java -name '*.java') tools/MetaProbe.java
 * 用法:
 *   java -cp /tmp/javabuild MetaProbe a.qzdb b.qzdb ... > /tmp/meta_java.json
 */
import com.qqzeng.qzdb.QzdbReader;

import java.io.File;

public final class MetaProbe {

    /** 最小 JSON 字符串转义，与其它语言 json 序列化的可比子集保持一致。 */
    private static String jsonString(String s) {
        if (s == null) s = "";
        StringBuilder sb = new StringBuilder(s.length() + 2);
        sb.append('"');
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"' -> sb.append("\\\"");
                case '\\' -> sb.append("\\\\");
                case '\n' -> sb.append("\\n");
                case '\r' -> sb.append("\\r");
                case '\t' -> sb.append("\\t");
                default -> {
                    if (c < 0x20) sb.append(String.format("\\u%04x", (int) c));
                    else sb.append(c);
                }
            }
        }
        return sb.append('"').toString();
    }

    public static void main(String[] args) throws Exception {
        StringBuilder out = new StringBuilder("[");
        for (int i = 0; i < args.length; i++) {
            if (i > 0) out.append(',');
            try (QzdbReader r = new QzdbReader.Builder(new File(args[i])).build()) {
                out.append('{');
                out.append(jsonString("file")).append(':').append(jsonString(new File(args[i]).getName())).append(',');
                out.append(jsonString("lang")).append(':').append(jsonString("java")).append(',');
                out.append(jsonString("edition")).append(':').append(jsonString(r.getEdition())).append(',');
                out.append(jsonString("edition_source")).append(':').append(jsonString(r.getEditionSource())).append(',');
                out.append(jsonString("version_mask")).append(':').append(r.getVersionMask()).append(',');
                out.append(jsonString("field_names_source")).append(':').append(jsonString(r.getFieldNamesSource())).append(',');
                out.append(jsonString("field_names")).append(":[");
                String[] names = r.getFieldNames();
                for (int j = 0; j < names.length; j++) {
                    if (j > 0) out.append(',');
                    out.append(jsonString(names[j]));
                }
                out.append("],");
                out.append(jsonString("group_count")).append(':').append(r.getGroupCount()).append(',');
                out.append(jsonString("pool_count")).append(':').append(r.getPoolCount()).append(',');
                out.append(jsonString("data_month")).append(':').append(jsonString(r.getDataMonth()));
                out.append('}');
            }
        }
        out.append(']');
        System.out.print(out);
    }
}
