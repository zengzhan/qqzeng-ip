package qzdb

import (
	"strconv"
	"strings"
)

// isJsonNumber 校验字符串是否为合法 JSON 数字（整数或小数，允许负号）。
func isJsonNumber(val string) bool {
	n := len(val)
	if n == 0 {
		return false
	}
	i := 0
	if val[0] == '-' {
		if n == 1 {
			return false
		}
		i = 1
	}
	digit, dot := false, false
	for ; i < n; i++ {
		c := val[i]
		if c >= '0' && c <= '9' {
			digit = true
		} else if c == '.' && !dot {
			dot = true
		} else {
			return false
		}
	}
	return digit
}

func escapeJson(s string) string {
	var sb strings.Builder
	sb.Grow(len(s) + 8)
	for i := 0; i < len(s); i++ {
		c := s[i]
		switch c {
		case '"':
			sb.WriteString("\\\"")
		case '\\':
			sb.WriteString("\\\\")
		case '\b':
			sb.WriteString("\\b")
		case '\f':
			sb.WriteString("\\f")
		case '\n':
			sb.WriteString("\\n")
		case '\r':
			sb.WriteString("\\r")
		case '\t':
			sb.WriteString("\\t")
		default:
			if c < 0x20 {
				sb.WriteString(`\u00`)
				sb.WriteByte(hexDigits[c>>4])
				sb.WriteByte(hexDigits[c&0xF])
			} else {
				sb.WriteByte(c)
			}
		}
	}
	return sb.String()
}

const hexDigits = "0123456789abcdef"

// ToJson 手写序列化：保持原始 snake_case 键；longitude/latitude/asn/geo_id 输出为 JSON 数字
// （无法解析则 null），其余为字符串。
func (g *GeoInfo) ToJson() string {
	if g == nil {
		return "{}"
	}
	// 预分配容量：键 + 值 + 标点符号
	var cap int
	for i, v := range g.Values {
		cap += len(g.FieldNames[i]) + len(v) + 10
	}
	var b strings.Builder
	b.Grow(cap)
	b.WriteByte('{')
	for i, name := range g.FieldNames {
		if i > 0 {
			b.WriteByte(',')
		}
		b.WriteByte('"')
		b.WriteString(escapeJson(name))
		b.WriteString("\":")
		val := ""
		if i < len(g.Values) {
			val = g.Values[i]
		}
		numeric := i < len(g.numeric) && g.numeric[i]
		switch {
		case val == "":
			if numeric {
				b.WriteString("null")
			} else {
				b.WriteString("\"\"")
			}
		case numeric:
			if isJsonNumber(val) {
				b.WriteString(val)
			} else {
				b.WriteString("null")
			}
		default:
			b.WriteByte('"')
			b.WriteString(escapeJson(val))
			b.WriteByte('"')
		}
	}
	b.WriteByte('}')
	return b.String()
}

// ---------- 语义化 Getter 全集（缺失返回 "" 或 nil） ----------

func (g *GeoInfo) GetCidr() string          { return "" } // CIDR 不是数据库字段（契约 §6）
func (g *GeoInfo) GetCountry() string       { return g.Get("country") }
func (g *GeoInfo) GetCountryEn() string     { return g.Get("country_en") }
func (g *GeoInfo) GetProvince() string      { return g.Get("province") }
func (g *GeoInfo) GetProvinceEn() string    { return g.Get("province_en") }
func (g *GeoInfo) GetCity() string          { return g.Get("city") }
func (g *GeoInfo) GetCityEn() string        { return g.Get("city_en") }
func (g *GeoInfo) GetDistrict() string      { return g.Get("district") }

// GetGeoId 返回 geo_id（long）；缺失返回 nil。
func (g *GeoInfo) GetGeoId() *int64 {
	v, ok := parseGeoID(g.Get("geo_id"))
	if !ok {
		return nil
	}
	return &v
}

// GetLongitude 返回 longitude（double）；缺失返回 nil。
func (g *GeoInfo) GetLongitude() *float64 {
	s := g.Get("longitude")
	if s == "" {
		return nil
	}
	f, err := strconv.ParseFloat(s, 64)
	if err != nil {
		return nil
	}
	return &f
}

// GetLatitude 返回 latitude（double）；缺失返回 nil。
func (g *GeoInfo) GetLatitude() *float64 {
	s := g.Get("latitude")
	if s == "" {
		return nil
	}
	f, err := strconv.ParseFloat(s, 64)
	if err != nil {
		return nil
	}
	return &f
}

func (g *GeoInfo) GetTimezone() string    { return g.Get("timezone") }
func (g *GeoInfo) GetIsp() string         { return g.Get("isp") }
func (g *GeoInfo) GetIspEn() string       { return g.Get("isp_en") }

// GetAsn 返回 asn（long）；缺失返回 nil。
func (g *GeoInfo) GetAsn() *int64 {
	v, ok := parseGeoID(g.Get("asn"))
	if !ok {
		return nil
	}
	return &v
}

func (g *GeoInfo) GetAsName() string     { return g.Get("as_name") }
func (g *GeoInfo) GetAsDomain() string   { return g.Get("as_domain") }

// GetUsageType 返回 UsageType（21 语义 + 未知兜底）。
func (g *GeoInfo) GetUsageType() UsageType {
	return ParseUsageType(g.Get("usage_type"))
}

func (g *GeoInfo) GetCountryAlpha2() string { return g.Get("country_alpha2") }
func (g *GeoInfo) GetCountryAlpha3() string { return g.Get("country_alpha3") }
func (g *GeoInfo) GetCurrencyCode() string  { return g.Get("currency_code") }
func (g *GeoInfo) GetCurrencyName() string  { return g.Get("currency_name") }
func (g *GeoInfo) GetPhonePrefix() string   { return g.Get("phone_prefix") }
func (g *GeoInfo) GetEmojiFlag() string     { return g.Get("emoji_flag") }
func (g *GeoInfo) GetLanguages() string     { return g.Get("languages") }
