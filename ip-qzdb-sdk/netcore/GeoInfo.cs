namespace QQZeng.Qzdb;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

/// <summary>Immutable result object returned by the QZDB query API. Exposes fields by name (case/underscore/hyphen-insensitive), strongly-typed getters, and serialization helpers (pipe / JSON / map).</summary>
public sealed class GeoInfo
{
    private readonly string[] _fieldNames;
    private readonly string[] _values;
    private readonly Dictionary<string, int>? _normMap;
    private readonly bool[]? _numericFlags;
    private string? _pipe; // lazily memoized ToPipe() result (immutable per instance)

    // Pre-bound standard field indices (avoids NormalizeKey and Dictionary lookup in hot-path getters)
    private readonly int _idxCountry = -1;
    private readonly int _idxCountryEn = -1;
    private readonly int _idxProvince = -1;
    private readonly int _idxProvinceEn = -1;
    private readonly int _idxCity = -1;
    private readonly int _idxCityEn = -1;
    private readonly int _idxDistrict = -1;
    private readonly int _idxIsp = -1;
    private readonly int _idxIspEn = -1;
    private readonly int _idxContinent = -1;
    private readonly int _idxContinentEn = -1;
    private readonly int _idxCountryCode = -1;
    private readonly int _idxAsn = -1;
    private readonly int _idxGeoId = -1;
    private readonly int _idxLongitude = -1;
    private readonly int _idxLatitude = -1;
    private readonly int _idxTimezone = -1;
    private readonly int _idxAsName = -1;
    private readonly int _idxAsDomain = -1;
    private readonly int _idxUsageType = -1;

    /// <summary>Constructs a GeoInfo from parallel field-name and value arrays. Arrays are cloned defensively; a null normMap or numericFlags is derived from the field names.</summary>
    public GeoInfo(string[] fieldNames, string[] values, Dictionary<string, int>? normMap, bool[]? numericFlags)
    {
        ArgumentNullException.ThrowIfNull(fieldNames);
        ArgumentNullException.ThrowIfNull(values);
        _fieldNames = (string[])fieldNames.Clone();
        _values = (string[])values.Clone();
        _normMap = normMap == null ? BuildNormalizedMap(_fieldNames) : new Dictionary<string, int>(normMap, StringComparer.Ordinal);
        _numericFlags = numericFlags == null ? null : (bool[])numericFlags.Clone();
        BindStandardIndices(_normMap, out _idxCountry, out _idxCountryEn, out _idxProvince, out _idxProvinceEn,
            out _idxCity, out _idxCityEn, out _idxDistrict, out _idxIsp, out _idxIspEn, out _idxContinent,
            out _idxContinentEn, out _idxCountryCode, out _idxAsn, out _idxGeoId, out _idxLongitude,
            out _idxLatitude, out _idxTimezone, out _idxAsName, out _idxAsDomain, out _idxUsageType);
    }

    internal GeoInfo(string[] fieldNames, string[] values, Dictionary<string, int>? normMap,
        bool[]? numericFlags, bool takeOwnership)
    {
        _fieldNames = fieldNames;
        _values = values;
        _normMap = normMap;
        _numericFlags = numericFlags;
        if (_normMap != null)
        {
            BindStandardIndices(_normMap, out _idxCountry, out _idxCountryEn, out _idxProvince, out _idxProvinceEn,
                out _idxCity, out _idxCityEn, out _idxDistrict, out _idxIsp, out _idxIspEn, out _idxContinent,
                out _idxContinentEn, out _idxCountryCode, out _idxAsn, out _idxGeoId, out _idxLongitude,
                out _idxLatitude, out _idxTimezone, out _idxAsName, out _idxAsDomain, out _idxUsageType);
        }
    }

    private static void BindStandardIndices(Dictionary<string, int> map,
        out int country, out int countryEn, out int province, out int provinceEn,
        out int city, out int cityEn, out int district, out int isp, out int ispEn,
        out int continent, out int continentEn, out int countryCode, out int asn,
        out int geoId, out int longitude, out int latitude, out int timezone,
        out int asName, out int asDomain, out int usageType)
    {
        map.TryGetValue("country", out country);
        map.TryGetValue("countryen", out countryEn);
        map.TryGetValue("province", out province);
        map.TryGetValue("provinceen", out provinceEn);
        map.TryGetValue("city", out city);
        map.TryGetValue("cityen", out cityEn);
        map.TryGetValue("district", out district);
        map.TryGetValue("isp", out isp);
        map.TryGetValue("ispen", out ispEn);
        map.TryGetValue("continent", out continent);
        map.TryGetValue("continenten", out continentEn);
        map.TryGetValue("countrycode", out countryCode);
        map.TryGetValue("asn", out asn);
        map.TryGetValue("geoid", out geoId);
        map.TryGetValue("longitude", out longitude);
        map.TryGetValue("latitude", out latitude);
        map.TryGetValue("timezone", out timezone);
        map.TryGetValue("asname", out asName);
        map.TryGetValue("asdomain", out asDomain);
        map.TryGetValue("usagetype", out usageType);
    }

    /// <summary>Field names in file order (defensive clone; safe to retain).</summary>
    public string[] FieldNames => (string[])_fieldNames.Clone();
    /// <summary>Field values in file order (defensive clone; safe to retain).</summary>
    public string[] Values => (string[])_values.Clone();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private string GetFast(int idx)
    {
        if ((uint)idx < (uint)_values.Length)
        {
            return _values[idx] ?? "";
        }
        return "";
    }

    /// <summary>Returns the value for the given field name. Matching is case-, underscore- and hyphen-insensitive; returns "" when not found or the name is empty.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string Get(string? name)
    {
        if (string.IsNullOrEmpty(name) || _normMap == null) return "";
        if (!_normMap.TryGetValue(NormalizeKey(name), out var idx) || idx >= _values.Length) return "";
        var v = _values[idx];
        return v ?? "";
    }

    /// <summary>Normalizes a field key by lowercasing ASCII letters and stripping underscores/hyphens; used for case-insensitive lookups.</summary>
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

    /// <summary>Builds a normalized-key to index map for the given field names.</summary>
    public static Dictionary<string, int> BuildNormalizedMap(string[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
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

    /// <summary>Returns true for fields whose values are numeric (asn, geoid, latitude, longitude).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsNumericFieldName(string? name)
    {
        if (name == null) return false;
        return NormalizeKey(name) is "asn" or "geoid" or "latitude" or "longitude";
    }

    /// <summary>Returns all values joined by '|' (memoized; empty string when there are no values).</summary>
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

    /// <summary>Returns a Dictionary mapping each field name to its value.</summary>
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

    /// <summary>Returns a compact JSON object; numeric fields are emitted as numbers or null.</summary>
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

    /// <summary>CIDR of the network containing this entry, or "" when unavailable.</summary>
    public string Cidr => GetCidr();
    /// <summary>Country name (localized).</summary>
    public string Country => GetCountry();
    /// <summary>Country name in English.</summary>
    public string CountryEn => GetCountryEn();
    /// <summary>Province / state name.</summary>
    public string Province => GetProvince();
    /// <summary>Province / state name in English.</summary>
    public string ProvinceEn => GetProvinceEn();
    /// <summary>City name.</summary>
    public string City => GetCity();
    /// <summary>City name in English.</summary>
    public string CityEn => GetCityEn();
    /// <summary>District / county name.</summary>
    public string District => GetDistrict();
    /// <summary>Geographic ID, or null when absent.</summary>
    public uint? GeoId => GetGeoId();
    /// <summary>Longitude as a double, or null when absent/invalid.</summary>
    public double? Longitude => GetLongitude();
    /// <summary>Latitude as a double, or null when absent/invalid.</summary>
    public double? Latitude => GetLatitude();
    /// <summary>IANA-style timezone string.</summary>
    public string Timezone => GetTimezone();
    /// <summary>ISP / operator name.</summary>
    public string Isp => GetIsp();
    /// <summary>ISP / operator name in English.</summary>
    public string IspEn => GetIspEn();
    /// <summary>Autonomous System Number, or null when absent.</summary>
    public uint? Asn => GetAsn();
    /// <summary>AS name.</summary>
    public string AsName => GetAsName();
    /// <summary>AS domain.</summary>
    public string AsDomain => GetAsDomain();
    /// <summary>Resolved usage-type classification (see <see cref="QQZeng.Qzdb.UsageType"/>).</summary>
    public UsageType UsageType => GetUsageType();
    /// <summary>ISO 3166-1 alpha-2 country code (redirects to country_code).</summary>
    public string CountryAlpha2 => GetCountryAlpha2();
    /// <summary>ISO 3166-1 alpha-3 country code.</summary>
    public string CountryAlpha3 => GetCountryAlpha3();
    /// <summary>Currency code.</summary>
    public string CurrencyCode => GetCurrencyCode();
    /// <summary>Currency name.</summary>
    public string CurrencyName => GetCurrencyName();
    /// <summary>Telephone country calling code.</summary>
    public string PhonePrefix => GetPhonePrefix();
    /// <summary>Country flag emoji.</summary>
    public string EmojiFlag => GetEmojiFlag();
    /// <summary>Comma-separated list of languages.</summary>
    public string Languages => GetLanguages();
    /// <summary>Continent name.</summary>
    public string Continent => GetContinent();
    /// <summary>Continent name in English.</summary>
    public string ContinentEn => GetContinentEn();
    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    public string CountryCode => GetCountryCode();

    /// <summary>Returns the CIDR of the network containing this entry (see <see cref="Cidr"/>).</summary>
    public string GetCidr() => Get("cidr");
    /// <summary>Returns the country name (see <see cref="Country"/>).</summary>
    public string GetCountry() => _idxCountry >= 0 ? GetFast(_idxCountry) : Get("country");
    /// <summary>Returns the country name in English.</summary>
    public string GetCountryEn() => _idxCountryEn >= 0 ? GetFast(_idxCountryEn) : Get("country_en");
    /// <summary>Returns the province / state name.</summary>
    public string GetProvince() => _idxProvince >= 0 ? GetFast(_idxProvince) : Get("province");
    /// <summary>Returns the province / state name in English.</summary>
    public string GetProvinceEn() => _idxProvinceEn >= 0 ? GetFast(_idxProvinceEn) : Get("province_en");
    /// <summary>Returns the city name.</summary>
    public string GetCity() => _idxCity >= 0 ? GetFast(_idxCity) : Get("city");
    /// <summary>Returns the city name in English.</summary>
    public string GetCityEn() => _idxCityEn >= 0 ? GetFast(_idxCityEn) : Get("city_en");
    /// <summary>Returns the district / county name.</summary>
    public string GetDistrict() => _idxDistrict >= 0 ? GetFast(_idxDistrict) : Get("district");

    /// <summary>Returns the geographic ID, or null when absent/invalid.</summary>
    public uint? GetGeoId()
    {
        var v = _idxGeoId >= 0 ? GetFast(_idxGeoId) : Get("geo_id");
        if (string.IsNullOrEmpty(v)) return null;
        return uint.TryParse(v, out var r) ? r : null;
    }

    /// <summary>Returns the longitude, or null when absent/invalid.</summary>
    public double? GetLongitude()
    {
        var v = _idxLongitude >= 0 ? GetFast(_idxLongitude) : Get("longitude");
        if (string.IsNullOrEmpty(v)) return null;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
    }

    /// <summary>Returns the latitude, or null when absent/invalid.</summary>
    public double? GetLatitude()
    {
        var v = _idxLatitude >= 0 ? GetFast(_idxLatitude) : Get("latitude");
        if (string.IsNullOrEmpty(v)) return null;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null;
    }

    /// <summary>Returns the timezone string.</summary>
    public string GetTimezone() => _idxTimezone >= 0 ? GetFast(_idxTimezone) : Get("timezone");
    /// <summary>Returns the ISP / operator name.</summary>
    public string GetIsp() => _idxIsp >= 0 ? GetFast(_idxIsp) : Get("isp");
    /// <summary>Returns the ISP / operator name in English.</summary>
    public string GetIspEn() => _idxIspEn >= 0 ? GetFast(_idxIspEn) : Get("isp_en");

    /// <summary>Returns the ASN, or null when absent/invalid.</summary>
    public uint? GetAsn()
    {
        var v = _idxAsn >= 0 ? GetFast(_idxAsn) : Get("asn");
        if (string.IsNullOrEmpty(v)) return null;
        return uint.TryParse(v, out var r) ? r : null;
    }

    /// <summary>Returns the AS name.</summary>
    public string GetAsName() => _idxAsName >= 0 ? GetFast(_idxAsName) : Get("as_name");
    /// <summary>Returns the AS domain.</summary>
    public string GetAsDomain() => _idxAsDomain >= 0 ? GetFast(_idxAsDomain) : Get("as_domain");

    /// <summary>Returns the resolved usage-type classification.</summary>
    public UsageType GetUsageType() => UsageType.Parse(_idxUsageType >= 0 ? GetFast(_idxUsageType) : Get("usage_type"));

    // 数据集以 country_code 存储 ISO 3166-1 alpha-2（如 "CN"），并不存在 country_alpha2 字段；
    // GetCountryAlpha2 重定向到 country_code 以返回真实二字码（历史返回 "" 为字段名笔误 bug）。
    /// <summary>Returns the ISO 3166-1 alpha-2 country code.</summary>
    public string GetCountryAlpha2() => Get("country_code");
    /// <summary>Returns the ISO 3166-1 alpha-3 country code.</summary>
    public string GetCountryAlpha3() => Get("country_alpha3");
    /// <summary>Returns the continent name.</summary>
    public string GetContinent() => _idxContinent >= 0 ? GetFast(_idxContinent) : Get("continent");
    /// <summary>Returns the continent name in English.</summary>
    public string GetContinentEn() => _idxContinentEn >= 0 ? GetFast(_idxContinentEn) : Get("continent_en");
    /// <summary>Returns the ISO 3166-1 alpha-2 country code.</summary>
    public string GetCountryCode() => _idxCountryCode >= 0 ? GetFast(_idxCountryCode) : Get("country_code");
    /// <summary>Returns the currency code.</summary>
    public string GetCurrencyCode() => Get("currency_code");
    /// <summary>Returns the currency name.</summary>
    public string GetCurrencyName() => Get("currency_name");
    /// <summary>Returns the telephone country calling code.</summary>
    public string GetPhonePrefix() => Get("phone_prefix");
    /// <summary>Returns the country flag emoji.</summary>
    public string GetEmojiFlag() => Get("emoji_flag");
    /// <summary>Returns the comma-separated language list.</summary>
    public string GetLanguages() => Get("languages");

    /// <summary>Returns the pipe-delimited representation (same as <see cref="ToPipe"/>).</summary>
    public override string ToString() => ToPipe();
}
