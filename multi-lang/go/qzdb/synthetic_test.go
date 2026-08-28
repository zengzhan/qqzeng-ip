package qzdb

import (
	"encoding/binary"
	"hash/crc32"
	"strings"
	"testing"
)

// buildSyntheticDB 在内存中构造一个最小但完全合法的 QZDB 文件（镜像 gen_synthetic_db.py）。
// 用于 Tier1 中需要数据库的断言（Reload / 资源释放 / 损坏文件 / CRC），避免依赖外部数据文件。
func buildSyntheticDB(t *testing.T) []byte {
	return buildSyntheticDBWith(t, "CHINANET")
}

// modifySyntheticASName 返回一个 as_name 文案不同的等价数据库（用于 Reload 测试）。
func modifySyntheticASName(t *testing.T, _ []byte, asName string) []byte {
	return buildSyntheticDBWith(t, asName)
}

func buildSyntheticDBWith(t *testing.T, asName string) []byte {
	t.Helper()
	const (
		headerSize  = 192
		fields      = 8
		poolIdxSize = 2
		ipRowSize   = 4
		geoGroups   = 1
	)

	align64 := func(n int) int { return (n + 63) &^ 63 }

	pools := [][]string{
		{"", "亚洲"},
		{"", "CN"},
		{"", "中国"},
		{"", "中国电信"},
		{"", "137702", "4134", "15169"},
		{"", asName, "Chinanet", "Google"},
		{"", "chinatelecom.cn", "google.com"},
		{"", "isp"},
	}
	geoEntries := [][]uint16{
		{0, 0, 0, 0, 0, 0, 0, 0},
		{1, 1, 1, 1, 1, 1, 1, 1},
		{1, 1, 1, 1, 2, 2, 2, 1},
		{1, 1, 1, 1, 3, 3, 3, 1},
	}
	type row struct{ geo, asn uint16 }
	ipRows := []row{{0, 0}, {0, 1}, {0, 2}, {0, 3}, {0, 1}}

	v4Ranges := []struct {
		lo    string
		hi    string
		rowID uint32
	}{
		{"114.114.0.0", "114.114.255.255", 1},
		{"223.5.0.0", "223.5.255.255", 2},
		{"8.8.0.0", "8.8.255.255", 3},
	}
	v6Ranges := []struct {
		ip    string
		rowID uint32
	}{
		{"2408:8000:9000::", 4},
	}

	ipv4ToUint := func(ip string) uint32 {
		var p [4]int
		var i int
		for _, c := range ip {
			if c == '.' {
				i++
				continue
			}
			p[i] = p[i]*10 + int(c-'0')
		}
		return uint32(p[0])<<24 | uint32(p[1])<<16 | uint32(p[2])<<8 | uint32(p[3])
	}
	ipv6ToUint128 := func(ip string) [2]uint64 {
		var full [8]uint16
		head, tail := splitHeadTail(ip)
		hi := 0
		for _, part := range strings.Split(head, ":") {
			if part == "" {
				continue
			}
			full[hi] = parseHexGroup(part)
			hi++
		}
		missing := 8 - hi
		if tail != "" {
			tailParts := strings.Split(tail, ":")
			for _, part := range tailParts {
				full[hi] = parseHexGroup(part)
				hi++
			}
		}
		_ = missing
		var hi64, lo64 uint64
		for i := 0; i < 4; i++ {
			hi64 = hi64<<16 | uint64(full[i])
		}
		for i := 4; i < 8; i++ {
			lo64 = lo64<<16 | uint64(full[i])
		}
		return [2]uint64{hi64, lo64}
	}

	// ---- offsets ----
	offRowSchema := headerSize
	cursor := offRowSchema + 12
	cursor = align64(cursor)
	offGroupSchema := cursor
	groupSchema := buildGroupSchemaGo(fields, len(geoEntries))
	cursor += len(groupSchema)
	cursor = align64(cursor)
	offV4Jump := cursor
	v4Jump := make([]byte, 65536*4)
	for _, r := range v4Ranges {
		start := ipv4ToUint(r.lo)
		hi16 := (start >> 16) & 0xFFFF
		binary.LittleEndian.PutUint32(v4Jump[hi16*4:], r.rowID|0x80000000)
	}
	cursor += len(v4Jump)
	cursor = align64(cursor)
	offV6Jump := cursor
	v6Jump := make([]byte, (1<<16)*4)
	for _, r := range v6Ranges {
		v := ipv6ToUint128(r.ip)
		hi := (v[0] >> (64 - 16)) & 0xFFFF
		binary.LittleEndian.PutUint32(v6Jump[hi*4:], r.rowID|0x80000000)
	}
	cursor += len(v6Jump)
	cursor = align64(cursor)
	offIPRow := cursor
	ipRow := make([]byte, len(ipRows)*ipRowSize)
	for i, r := range ipRows {
		binary.LittleEndian.PutUint16(ipRow[i*ipRowSize:], r.geo)
		binary.LittleEndian.PutUint16(ipRow[i*ipRowSize+2:], r.asn)
	}
	cursor += len(ipRow)
	cursor = align64(cursor)
	offGeoEntries := cursor
	metaTbl := make([]byte, 0, 8)
	metaTbl = append(metaTbl, byte(geoGroups))
	metaTbl = binary.LittleEndian.AppendUint32(metaTbl, fields)
	metaTbl = binary.LittleEndian.AppendUint32(metaTbl, uint32(len(geoEntries)))
	metaTbl = append(metaTbl, 0x02, 0x00) // dimMask=asn (uint16 LE)
	cursor += len(metaTbl)
	// GeoEntryOffsets[0] = 64
	groupDataStart := offGeoEntries + 64
	geoData := make([]byte, 0, len(geoEntries)*fields*2)
	for _, e := range geoEntries {
		for _, v := range e {
			geoData = binary.LittleEndian.AppendUint16(geoData, v)
		}
	}
	for len(geoData)%64 != 0 {
		geoData = append(geoData, 0)
	}
	cursor = groupDataStart + len(geoData)
	cursor = align64(cursor)
	offPools := cursor
	poolsBlob := make([]byte, 0)
	for i := 0; i < fields; i++ {
		strs := pools[i]
		var data []byte
		offsets := []uint32{0}
		for _, s := range strs {
			data = append(data, []byte(s)...)
			offsets = append(offsets, uint32(len(data)))
		}
		poolsBlob = binary.LittleEndian.AppendUint32(poolsBlob, uint32(len(strs)))
		poolsBlob = binary.LittleEndian.AppendUint32(poolsBlob, uint32(len(data)))
		for _, o := range offsets {
			poolsBlob = binary.LittleEndian.AppendUint32(poolsBlob, o)
		}
		poolsBlob = append(poolsBlob, data...)
	}
	cursor += len(poolsBlob)
	cursor = align64(cursor)
	offMeta := cursor
	meta := buildMetaGo()
	cursor += len(meta)

	blob := make([]byte, cursor)
	// header
	copy(blob[0:4], "QZDB")
	blob[4] = 1
	binary.LittleEndian.PutUint16(blob[6:], 2)    // version mask: asn=0x02 (NEW one-hot §3.1)
	binary.LittleEndian.PutUint16(blob[8:], 0x37) // flags V4|V6|meta|v4node24|v6node24
	blob[10] = 16
	blob[11] = 16
	blob[12] = fields
	blob[13] = poolIdxSize
	binary.LittleEndian.PutUint16(blob[14:], uint16(len(geoEntries)))
	binary.LittleEndian.PutUint32(blob[20:], uint32(len(ipRows)))
	binary.LittleEndian.PutUint32(blob[24:], uint32(len(v4Ranges)))
	binary.LittleEndian.PutUint32(blob[28:], uint32(len(v6Ranges)))
	binary.LittleEndian.PutUint32(blob[32:], 20260805)
	binary.LittleEndian.PutUint32(blob[36:], headerSize)
	binary.LittleEndian.PutUint64(blob[40:], uint64(offRowSchema))
	binary.LittleEndian.PutUint64(blob[48:], uint64(offGroupSchema))
	binary.LittleEndian.PutUint64(blob[64:], uint64(offV4Jump))
	binary.LittleEndian.PutUint64(blob[72:], 0)
	binary.LittleEndian.PutUint64(blob[80:], uint64(offV6Jump))
	binary.LittleEndian.PutUint64(blob[88:], 0)
	binary.LittleEndian.PutUint64(blob[96:], uint64(offIPRow))
	binary.LittleEndian.PutUint64(blob[104:], uint64(offGeoEntries))
	binary.LittleEndian.PutUint64(blob[136:], uint64(offPools))
	binary.LittleEndian.PutUint64(blob[144:], uint64(offMeta))
	binary.LittleEndian.PutUint32(blob[152:], 0) // v4NodeCount
	binary.LittleEndian.PutUint32(blob[156:], 0) // v6NodeCount
	binary.LittleEndian.PutUint32(blob[160:], ipRowSize)
	binary.LittleEndian.PutUint32(blob[164:], geoGroups)
	binary.LittleEndian.PutUint64(blob[168:], 64) // GeoEntryOffsets[0]

	copy(blob[offRowSchema:], rowSchemaGo())
	copy(blob[offGroupSchema:], groupSchema)
	copy(blob[offV4Jump:], v4Jump)
	copy(blob[offV6Jump:], v6Jump)
	copy(blob[offIPRow:], ipRow)
	copy(blob[offGeoEntries:], metaTbl)
	copy(blob[groupDataStart:], geoData)
	copy(blob[offPools:], poolsBlob)
	copy(blob[offMeta:], meta)

	// CRC32（canonical：偏移 16~19 填 0）
	crc := crc32.ChecksumIEEE(blob)
	binary.LittleEndian.PutUint32(blob[16:], crc)
	return blob
}

func rowSchemaGo() []byte {
	return []byte{
		2, 4, 0, 0,
		0, 2, 0, 0,
		1, 2, 2, 0,
	}
}

func buildGroupSchemaGo(fields, entryCount int) []byte {
	out := make([]byte, 0)
	out = binary.LittleEndian.AppendUint16(out, 1) // groupSchemaCount
	out = binary.LittleEndian.AppendUint16(out, 2) // groupId: asn=0x02 (NEW one-hot §3.1)
	out = binary.LittleEndian.AppendUint16(out, uint16(fields))
	out = binary.LittleEndian.AppendUint32(out, uint32(entryCount))
	out = binary.LittleEndian.AppendUint32(out, uint32(fields*2)) // stride
	out = binary.LittleEndian.AppendUint32(out, 0)                // flags
	for i := 0; i < fields; i++ {
		out = binary.LittleEndian.AppendUint16(out, uint16(i))   // fid
		out = append(out, 2)                                     // width
		out = append(out, 0)                                     // fieldFlags
		out = binary.LittleEndian.AppendUint32(out, uint32(i*2)) // offset
		out = binary.LittleEndian.AppendUint32(out, 0)           // poolSectionId
	}
	return out
}

func buildMetaGo() []byte {
	entries := []struct {
		t   byte
		val string
	}{
		{1, "asn"},
		{2, "continent|country_code|country|isp|asn|as_name|as_domain|usage_type"},
		{3, "synthetic test database"},
		{4, "asn"},
		{5, "2026-07"}, // v2.4 data_month：权威期号（Header BuildDate=20260805 仅为回落）
		{6, "global"},  // v2.4 scope
	}
	out := make([]byte, 0)
	for _, e := range entries {
		b := []byte(e.val)
		out = append(out, e.t, 0)
		out = binary.LittleEndian.AppendUint16(out, uint16(len(b)))
		out = append(out, b...)
	}
	return out
}

func splitHeadTail(ip string) (string, string) {
	for i := 0; i+1 < len(ip); i++ {
		if ip[i] == ':' && ip[i+1] == ':' {
			return ip[:i], ip[i+2:]
		}
	}
	return ip, ""
}
