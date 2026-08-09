namespace QQZeng.Qzdb;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

public sealed class GeoInfo
{
    private readonly string[] _fieldNames;
    private readonly string[] _values;
    private readonly Dictionary<string, int>? _normMap;
    private readonly bool[]? _numericFlags;
    private string? _pipe; // lazily memoized ToPipe() result (immutable per instance)

    public GeoInfo(string[] fieldNames, string[] values, Dictionary<string, int>? normMap, bool[]? numericFlags)
    {
        ArgumentNullException.ThrowIfNull(fieldNames);
        ArgumentNullException.ThrowIfNull(values);
        _fieldNames = (string[])fieldNames.Clone();
        _values = (string[])values.Clone();
        _normMap = normMap == null ? BuildNormalizedMap(_fieldNames) : new Dictionary<string, int>(normMap, StringComparer.Ordinal);
        _numericFlags = numericFlags == null ? null : (bool[])numericFlags.Clone();
    }

    internal GeoInfo(string[] fieldNames, string[] values, Dictionary<string, int>? normMap,
        bool[]? numericFlags, bool takeOwnership)
    {
        _fieldNames = fieldNames;
        _values = values;
        _normMap = normMap;
        _numericFlags = numericFlags;
    }

    public string[] FieldNames => (string[])_fieldNames.Clone();
    public string[] Values => (string[])_values.Clone();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string Get(string? name)
    {
        if (string.IsNullOrEmpty(name) || _normMap == null) return "";
        if (!_normMap.TryGetValue(NormalizeKey(name), out var idx) || idx >= _values.Length) return "";
        var v = _values[idx];
        return v ?? "";
    }

    public static string NormalizeKey(string? key)
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
                if (c != '_' && c != '-') buf[k++] = char.IsAsciiLetter(c) ? char.ToLowerInvariant(c) : c;
            }
            return new string(buf[..k]);
        }
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++)
        {
            char c = key[i];
            if (c != '_' && c != '-') sb.Append(char.IsAsciiLetter(c) ? char.ToLowerInvariant(c) : c);
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
    public static bool IsNumericFieldName(string? name)
    {
        if (name == null) return false;
        return NormalizeKey(name) is "asn" or "geoid" or "latitude" or "longitude";
    }

    public string ToPipe()
    {
        if (_values.Length == 0) return "";
        var cached = _pipe;
        if (cached != null) return cached;
        var sb = new StringBuilder();
        for (int i = 0; i < _fieldNames.Length; i++)
        {
            if (i > 0) sb.Append('|');
            if (i < _values.Length && _values[i] != null) sb.Append(_values[i]);
        }
        _pipe = sb.ToString();
        return _pipe;
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
        if (i >= val.Length) return false;
        if (val[i] == '0')
        {
            i++;
            if (i < val.Length && val[i] is >= '0' and <= '9') return false;
        }
        else
        {
            if (val[i] is < '1' or > '9') return false;
            while (++i < val.Length && val[i] is >= '0' and <= '9') { }
        }
        if (i < val.Length && val[i] == '.')
        {
            i++;
            int fractionStart = i;
            while (i < val.Length && val[i] is >= '0' and <= '9') i++;
            if (i == fractionStart) return false;
        }
        if (i < val.Length && (val[i] == 'e' || val[i] == 'E'))
        {
            i++;
            if (i < val.Length && (val[i] == '+' || val[i] == '-')) i++;
            int exponentStart = i;
            while (i < val.Length && val[i] is >= '0' and <= '9') i++;
            if (i == exponentStart) return false;
        }
        return i == val.Length;
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

    public string Cidr => GetCidr();
    public string Country => GetCountry();
    public string CountryEn => GetCountryEn();
    public string Province => GetProvince();
    public string ProvinceEn => GetProvinceEn();
    public string City => GetCity();
    public string CityEn => GetCityEn();
    public string District => GetDistrict();
    public uint? GeoId => GetGeoId();
    public double? Longitude => GetLongitude();
    public double? Latitude => GetLatitude();
    public string Timezone => GetTimezone();
    public string Isp => GetIsp();
    public string IspEn => GetIspEn();
    public uint? Asn => GetAsn();
    public string AsName => GetAsName();
    public string AsDomain => GetAsDomain();
    public UsageType UsageType => GetUsageType();
    public string CountryAlpha2 => GetCountryAlpha2();
    public string CountryAlpha3 => GetCountryAlpha3();
    public string CurrencyCode => GetCurrencyCode();
    public string CurrencyName => GetCurrencyName();
    public string PhonePrefix => GetPhonePrefix();
    public string EmojiFlag => GetEmojiFlag();
    public string Languages => GetLanguages();
    public string Continent => GetContinent();
    public string ContinentEn => GetContinentEn();
    public string CountryCode => GetCountryCode();

    public string GetCidr() => Get("cidr");
    public string GetCountry() => Get("country");
    public string GetCountryEn() => Get("country_en");
    public string GetProvince() => Get("province");
    public string GetProvinceEn() => Get("province_en");
    public string GetCity() => Get("city");
    public string GetCityEn() => Get("city_en");
    public string GetDistrict() => Get("district");

    public uint? GetGeoId()
    {
        var v = Get("geo_id");
        if (string.IsNullOrEmpty(v)) return null;
        return uint.TryParse(v, out var r) ? r : null;
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

    public uint? GetAsn()
    {
        var v = Get("asn");
        if (string.IsNullOrEmpty(v)) return null;
        return uint.TryParse(v, out var r) ? r : null;
    }

    public string GetAsName() => Get("as_name");
    public string GetAsDomain() => Get("as_domain");

    public UsageType GetUsageType() => UsageType.FromString(Get("usage_type"));

    // 数据集以 country_code 存储 ISO 3166-1 alpha-2（如 "CN"），并不存在 country_alpha2 字段；
    // GetCountryAlpha2 重定向到 country_code 以返回真实二字码（历史返回 "" 为字段名笔误 bug）。
    public string GetCountryAlpha2() => Get("country_code");
    public string GetCountryAlpha3() => Get("country_alpha3");
    public string GetContinent() => Get("continent");
    public string GetContinentEn() => Get("continent_en");
    public string GetCountryCode() => Get("country_code");
    public string GetCurrencyCode() => Get("currency_code");
    public string GetCurrencyName() => Get("currency_name");
    public string GetPhonePrefix() => Get("phone_prefix");
    public string GetEmojiFlag() => Get("emoji_flag");
    public string GetLanguages() => Get("languages");

    public override string ToString() => ToPipe();
}
