namespace Qzdb;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

public sealed class GeoInfo
{
    private readonly string[] _fieldNames;
    private readonly string[] _values;
    private readonly Dictionary<string, int>? _normMap;
    private readonly bool[]? _numericFlags;

    public GeoInfo(string[] fieldNames, string[] values, Dictionary<string, int>? normMap, bool[]? numericFlags)
    {
        _fieldNames = fieldNames;
        _values = values;
        _normMap = normMap;
        _numericFlags = numericFlags;
    }

    public string[] FieldNames => (string[])_fieldNames.Clone();
    public string[] Values => (string[])_values.Clone();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string Get(string name)
    {
        if (string.IsNullOrEmpty(name) || _normMap == null) return "";
        if (!_normMap.TryGetValue(NormalizeKey(name), out var idx) || idx >= _values.Length) return "";
        var v = _values[idx];
        return v ?? "";
    }

    public static string NormalizeKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        int n = key.Length;
        if (n <= 64)
        {
            Span<char> buf = stackalloc char[n];
            int k = 0;
            for (int i = 0; i < n; i++)
            {
                char c = key[i];
                if (c != '_') buf[k++] = char.IsAsciiLetter(c) ? char.ToLowerInvariant(c) : c;
            }
            return new string(buf[..k]);
        }
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++)
        {
            char c = key[i];
            if (c != '_') sb.Append(char.IsAsciiLetter(c) ? char.ToLowerInvariant(c) : c);
        }
        return sb.ToString();
    }

    public static Dictionary<string, int> BuildNormalizedMap(string[] fields)
    {
        var map = new Dictionary<string, int>(fields.Length * 2);
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i] != null)
            {
                var norm = NormalizeKey(fields[i]);
                map.TryAdd(norm, i);
            }
        }
        return map;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNumericFieldName(string name)
    {
        if (name == null) return false;
        return name.Length switch
        {
            3 => name.Equals("asn", StringComparison.OrdinalIgnoreCase),
            6 => name.Equals("geoid", StringComparison.OrdinalIgnoreCase) || name.Equals("geo_id", StringComparison.OrdinalIgnoreCase),
            8 => name.Equals("latitude", StringComparison.OrdinalIgnoreCase),
            9 => name.Equals("longitude", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public string ToPipe()
    {
        if (_values.Length == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < _fieldNames.Length; i++)
        {
            if (i > 0) sb.Append('|');
            if (i < _values.Length && _values[i] != null) sb.Append(_values[i]);
        }
        return sb.ToString();
    }

    public Dictionary<string, string> ToMap()
    {
        var map = new Dictionary<string, string>(_fieldNames.Length * 2);
        for (int i = 0; i < _fieldNames.Length; i++)
        {
            var val = (i < _values.Length && _values[i] != null) ? _values[i] : "";
            map[_fieldNames[i]] = val;
        }
        return map;
    }

    public string ToJson()
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        bool first = true;
        for (int i = 0; i < _fieldNames.Length; i++)
        {
            if (_fieldNames[i] == null) continue;
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(EscapeJson(_fieldNames[i])).Append("\":");
            bool numeric = (_numericFlags != null && i < _numericFlags.Length && _numericFlags[i])
                || (_numericFlags == null && IsNumericFieldName(_fieldNames[i]));
            if (i >= _values.Length || _values[i] == null || _values[i].Length == 0)
                sb.Append(numeric ? "null" : "\"\"");
            else if (numeric)
            {
                var v = _values[i];
                if (IsJsonNumber(v)) sb.Append(v);
                else sb.Append("null");
            }
            else sb.Append('"').Append(EscapeJson(_values[i])).Append('"');
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static bool IsJsonNumber(string val)
    {
        if (val.Length == 0) return false;
        int i = 0;
        if (val[0] == '-') { if (val.Length == 1) return false; i = 1; }
        bool digit = false, dot = false;
        for (; i < val.Length; i++)
        {
            char c = val[i];
            if (c is >= '0' and <= '9') digit = true;
            else if (c == '.' && !dot) dot = true;
            else return false;
        }
        return digit;
    }

    private static readonly char[] HexChars = "0123456789abcdef".ToCharArray();
    private static string EscapeJson(string s)
    {
        var sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (s[i] < 0x20)
                    {
                        sb.Append('\\').Append('u').Append(HexChars[(s[i] >> 12) & 0xF])
                          .Append(HexChars[(s[i] >> 8) & 0xF]).Append(HexChars[(s[i] >> 4) & 0xF])
                          .Append(HexChars[s[i] & 0xF]);
                    }
                    else sb.Append(s[i]);
                    break;
            }
        }
        return sb.ToString();
    }

    public string GetCidr() => Get("cidr");
    public string GetCountry() => Get("country");
    public string GetCountryEn() => Get("country_en");
    public string GetProvince() => Get("province");
    public string GetProvinceEn() => Get("province_en");
    public string GetCity() => Get("city");
    public string GetCityEn() => Get("city_en");
    public string GetDistrict() => Get("district");

    public ulong? GetGeoId()
    {
        var v = Get("geo_id");
        if (string.IsNullOrEmpty(v)) return null;
        return ulong.TryParse(v, out var r) ? r : null;
    }

    public double? GetLongitude()
    {
        var v = Get("longitude");
        if (string.IsNullOrEmpty(v)) return null;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
    }

    public double? GetLatitude()
    {
        var v = Get("latitude");
        if (string.IsNullOrEmpty(v)) return null;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
    }

    public string GetTimezone() => Get("timezone");
    public string GetIsp() => Get("isp");
    public string GetIspEn() => Get("isp_en");

    public ulong? GetAsn()
    {
        var v = Get("asn");
        if (string.IsNullOrEmpty(v)) return null;
        return ulong.TryParse(v, out var r) ? r : null;
    }

    public string GetAsName() => Get("as_name");
    public string GetAsDomain() => Get("as_domain");

    public UsageType GetUsageType() => UsageType.FromString(Get("usage_type"));

    public string GetCountryAlpha2() => Get("country_alpha2");
    public string GetCountryAlpha3() => Get("country_alpha3");
    public string GetCurrencyCode() => Get("currency_code");
    public string GetCurrencyName() => Get("currency_name");
    public string GetPhonePrefix() => Get("phone_prefix");
    public string GetEmojiFlag() => Get("emoji_flag");
    public string GetLanguages() => Get("languages");

    public override string ToString() => ToPipe();
}
