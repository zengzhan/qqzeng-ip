// Package qzdb 是 QZDB 离线 IP 地理定位数据库的高性能 Go SDK。
//
// 设计要点（对齐 API_CONTRACT.md v2.4）：
//   - 不可变快照（Snapshot）+ atomic.Pointer 原子替换：查询路径无锁、对快照只读。
//   - per-snapshot 有界无锁 GeoInfo 缓存：以 row_id 为键、开放寻址；
//     碰撞只重算、绝不返回错值；缓存命中趋近零分配。
//   - SENTINEL 哨兵位在解码 row_id 之前剥离。
//   - 原生浮点字段严格 6 位小数（整数值无小数点，NaN/Inf 为 ""）。
//   - 构造期 Fail-Closed：Magic / Header / CRC / 截断任一异常均拒绝初始化。
package qzdb

import (
	"encoding/binary"
	"errors"
	"fmt"
	"hash/crc32"
	"math"
	"math/bits"
	"net/netip"
	"os"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
)

// SENTINEL 高位哨兵位（32 位节点）。
const SENTINEL uint32 = 0x80000000

// SENTINEL_MASK_24 / SENTINEL_MASK_31 用于剥离 24/32 位节点的哨兵位。
const (
	SENTINEL_MASK_24 uint32 = 0x7FFFFF
	SENTINEL_MASK_31 uint32 = 0x7FFFFFFF
)

const maxTrieWalkSteps = 1000

// -----------------------------------------------------------------------------
// 版本档次与字段名的自描述契约（FORMAT §10.3）
//
// Header.VersionMask（偏移 6）与每个 GROUP_SCHEMA 组头的 groupId 是同一套 one-hot
// 版本位掩码，这才是判定档次的权威信号；字段个数只能作为最后兜底。
// -----------------------------------------------------------------------------

// EditionByBit 把 one-hot 位序号映射到档次名：bit0..bit4。
var EditionByBit = [...]string{"std", "asn", "pro", "max", "ult"}

// EditionFieldNames 各档次的规范字段顺序（FORMAT 附录 1）。
// 仅在文件没有 Metadata field_names 时使用，永远不覆盖文件自带的字段名。
var EditionFieldNames = map[string][]string{
	"std": {"continent", "country_code", "country", "province", "city", "isp"},
	"asn": {"continent", "country_code", "country", "isp", "asn", "as_name", "as_domain", "usage_type"},
	"pro": {"continent", "country_code", "country", "province", "city", "district", "geo_id",
		"longitude", "latitude", "timezone", "isp"},
	"max": {"continent", "country_code", "country", "province", "city", "district", "geo_id",
		"longitude", "latitude", "timezone", "isp", "asn", "as_name", "as_domain", "usage_type"},
	"ult": {"continent", "continent_en", "country_code", "country_alpha3", "country", "country_en",
		"province", "province_en", "city", "city_en", "district", "district_en", "geo_id",
		"longitude", "latitude", "timezone", "languages", "currency_code", "phone_prefix",
		"emoji_flag", "isp", "asn", "as_name", "as_domain", "usage_type"},
}

// 来源标记：8 种语言取值完全一致，跨语言对拍可直接比较。
const (
	EditionSourceVersionMask = "version_mask" // 来自 VersionMask/groupId（权威）
	EditionSourceMetadata    = "metadata"     // 来自 Metadata primary_version/version_list
	EditionSourceInferred    = "inferred"     // 兜底：字段数唯一匹配
	EditionSourceUnknown     = "unknown"      // 确实判定不出，不臆造

	FieldNamesSourceMetadata  = "metadata"  // 文件自带 Metadata field_names
	FieldNamesSourceEdition   = "edition"   // 已知档次的规范表
	FieldNamesSourceSynthetic = "synthetic" // field_0..field_N-1 占位符
)

// editionByFieldCount 反向索引：字段数 -> 档次，仅在该字段数唯一时有值。
var editionByFieldCount = func() map[int]string {
	m := make(map[int]string, len(EditionFieldNames))
	for ed, names := range EditionFieldNames {
		if _, dup := m[len(names)]; dup {
			m[len(names)] = "" // 有歧义 -> 不猜
			continue
		}
		m[len(names)] = ed
	}
	return m
}()

// EditionFromMask 把 one-hot 版本位掩码解析成档次名；非 one-hot 返回 ""。
func EditionFromMask(mask uint16) string {
	if mask == 0 || mask&(mask-1) != 0 {
		return "" // 0 或多于一位 -> 不是单一档次
	}
	bit := bits.TrailingZeros16(mask)
	if bit < len(EditionByBit) {
		return EditionByBit[bit]
	}
	return ""
}

// Snapshot 是不可变的只读数据视图。构造完成后不再修改，可安全被多 goroutine 并发读取。
type Snapshot struct {
	data    []byte
	release func() // mmap 释放回调；字节加载时为 nil

	refCount atomic.Int32 // 并发读取引用计数，防止 munmap 与读取竞态

	groupIndex int

	// Header
	flags      uint16
	hasV4      bool
	hasV6      bool
	v4Node24   bool
	v6Node24   bool
	v6JumpBits int
	poolCount  int
	poolIdxSize int
	rowCount   int
	v4NodeCount uint32
	v6NodeCount uint32
	ipRowSize  int
	geoEntryGroupCount int

	// Offsets
	offV4Jump    uint64
	offV4Nodes   uint64
	offV6Jump    uint64
	offV6Nodes   uint64
	offIPRow     uint64
	offGeoEntries uint64
	offPools     uint64
	offMeta      uint64
	offRowSchema uint64
	offGroupSchema uint64

	rowGeoWidth  int
	rowAsnWidth  int
	rowUsageWidth int

	// Group layout
	actualGroups       int
	groupFieldCounts   []int
	groupEntryCounts   []uint32
	groupDimMasks      []uint16
	groupEntryOffsets  []uint64
	groupStrides       []int
	groupFieldWidths   [][]int
	groupFieldOffsets  [][]int
	groupFieldNative   [][]bool
	groupFieldNativeType [][]int
	groupFieldIds      [][]uint16
	groupPools         [][][]string

	// Field metadata
	fieldNames   []string
	normalizedMap map[string]int
	numericFlags []bool

	// Meta accessors
	version          string
	description      string
	edition          string
	versionMask      uint16
	editionSource    string
	fieldNamesSource string
	groupIds         []uint16
	dataMonth        string
	buildTimeStr     string
	scope            string

	storedCrc   uint32
	crcOnce     sync.Once
	canonicalCrc uint32

	geoCache *geoCache
}

// QzdbReader 是面向用户的查询入口。内部仅持有一个原子指针指向当前快照。
type QzdbReader struct {
	snap atomic.Pointer[Snapshot]
}

// ---------- 基础 LE 读取 ----------

// boundsPanic 是解析期越界访问的统一哨兵，由 buildSnapshot 的 recover 收口为 ErrCodeOutOfBounds，
// 使畸形 .qzdb 文件 Fail-Closed（返回错误而非 panic 导致宿主进程崩溃）。
type boundsPanic struct{ msg string }

func (e *boundsPanic) Error() string { return e.msg }

func safeReadU16(b []byte, off uint64) uint16 {
	if off+2 > uint64(len(b)) {
		panic(&boundsPanic{fmt.Sprintf("readU16 OOB: off=%d len=%d", off, len(b))})
	}
	return binary.LittleEndian.Uint16(b[off:])
}
func safeReadU32(b []byte, off uint64) uint32 {
	if off+4 > uint64(len(b)) {
		panic(&boundsPanic{fmt.Sprintf("readU32 OOB: off=%d len=%d", off, len(b))})
	}
	return binary.LittleEndian.Uint32(b[off:])
}
func safeReadU64(b []byte, off uint64) uint64 {
	if off+8 > uint64(len(b)) {
		panic(&boundsPanic{fmt.Sprintf("readU64 OOB: off=%d len=%d", off, len(b))})
	}
	return binary.LittleEndian.Uint64(b[off:])
}

func (s *Snapshot) readU24(off uint64) uint32 {
	if off+3 > uint64(len(s.data)) {
		panic(&boundsPanic{fmt.Sprintf("readU24 OOB: off=%d len=%d", off, len(s.data))})
	}
	d := s.data
	return uint32(d[off]) | uint32(d[off+1])<<8 | uint32(d[off+2])<<16
}
func (s *Snapshot) readU48(off uint64) uint64 {
	if off+6 > uint64(len(s.data)) {
		panic(&boundsPanic{fmt.Sprintf("readU48 OOB: off=%d len=%d", off, len(s.data))})
	}
	d := s.data
	return uint64(d[off]) | uint64(d[off+1])<<8 | uint64(d[off+2])<<16 |
		uint64(d[off+3])<<24 | uint64(d[off+4])<<32 | uint64(d[off+5])<<40
}
func (s *Snapshot) readUintWidth(off uint64, width int) uint32 {
	switch {
	case width <= 1:
		if off >= uint64(len(s.data)) {
			panic(&boundsPanic{fmt.Sprintf("readUintWidth OOB: off=%d len=%d", off, len(s.data))})
		}
		return uint32(s.data[off])
	case width == 2:
		return uint32(safeReadU16(s.data, off))
	case width == 3:
		return s.readU24(off)
	default:
		return safeReadU32(s.data, off)
	}
}

// ---------- 快照加载（Fail-Closed） ----------

func buildSnapshot(data []byte, release func(), groupIndex int, verifyCrc bool) (snap *Snapshot, err error) {
	// 收口解析期越界访问（畸形文件 DoS）：仅捕获 boundsPanic 哨兵，其余 panic 照常上浮。
	defer func() {
		if r := recover(); r != nil {
			if bp, ok := r.(*boundsPanic); ok {
				snap = nil
				err = newErr(ErrCodeOutOfBounds, bp.msg)
				return
			}
			panic(r)
		}
	}()
	if len(data) < 192 {
		return nil, newErr(ErrCodeBadHeader, "file too small for QZDB header")
	}
	s := &Snapshot{data: data, release: release, groupIndex: groupIndex}

	if err := s.parseHeader(); err != nil {
		return nil, err
	}
	if err := s.parseRowSchema(); err != nil {
		return nil, err
	}
	if err := s.parseGroups(); err != nil {
		return nil, err
	}
	if err := s.parseMetadata(); err != nil {
		return nil, err
	}
	if verifyCrc {
		if !s.verifyCrcNow() {
			return nil, newErr(ErrCodeCorrupted,
				fmt.Sprintf("crc32 checksum mismatch: stored=0x%08x calculated=0x%08x", s.storedCrc, s.computeCanonicalCrc()))
		}
	}
	if err := s.parsePools(); err != nil {
		return nil, err
	}
	s.storedCrc = safeReadU32(data, 16)
	s.geoCache = newGeoCache(1 << 18)
	return s, nil
}

func (s *Snapshot) parseHeader() error {
	d := s.data
	if string(d[:4]) != "QZDB" {
		return newErr(ErrCodeBadMagic, "invalid magic, expected QZDB")
	}
	if d[4] != 1 {
		return newErr(ErrCodeUnsupported,
			fmt.Sprintf("unsupported format version: %d (only version 1 is supported)", d[4]))
	}
	// VersionMask（偏移 6）：one-hot 版本位掩码，判定档次的权威信号
	s.versionMask = safeReadU16(d, 6)

	s.flags = safeReadU16(d, 8)
	s.hasV4 = s.flags&1 != 0
	s.hasV6 = s.flags&2 != 0
	s.v4Node24 = s.flags&0x10 != 0
	s.v6Node24 = s.flags&0x20 != 0

	s.v6JumpBits = int(d[11])
	if s.v6JumpBits == 0 {
		s.v6JumpBits = 16
	}
	if s.v6JumpBits < 8 || s.v6JumpBits > 20 {
		return newErr(ErrCodeInvalidParam, fmt.Sprintf("v6JumpBits out of range [8,20]: %d", s.v6JumpBits))
	}
	s.poolCount = int(d[12])
	s.poolIdxSize = int(d[13])
	if s.poolIdxSize != 2 && s.poolIdxSize != 3 {
		return newErr(ErrCodeInvalidParam, fmt.Sprintf("poolIdxSize must be 2 or 3, got %d", s.poolIdxSize))
	}
	s.rowCount = int(safeReadU32(d, 20))
	s.offRowSchema = safeReadU64(d, 40)
	s.offGroupSchema = safeReadU64(d, 48)
	s.offV4Jump = safeReadU64(d, 64)
	s.offV4Nodes = safeReadU64(d, 72)
	s.offV6Jump = safeReadU64(d, 80)
	s.offV6Nodes = safeReadU64(d, 88)
	s.offIPRow = safeReadU64(d, 96)
	s.offGeoEntries = safeReadU64(d, 104)
	s.offPools = safeReadU64(d, 136)
	s.offMeta = safeReadU64(d, 144)
	s.v4NodeCount = safeReadU32(d, 152)
	s.v6NodeCount = safeReadU32(d, 156)
	rs := int(safeReadU32(d, 160))
	if rs < 1 || rs > 64 {
		return newErr(ErrCodeInvalidParam, fmt.Sprintf("ipRowSize out of range [1,64]: %d", rs))
	}
	s.ipRowSize = rs
	gc := int(safeReadU32(d, 164))
	if gc < 1 || gc > 255 {
		return newErr(ErrCodeInvalidParam, fmt.Sprintf("geoEntryGroupCount out of range [1,255]: %d", gc))
	}
	s.geoEntryGroupCount = gc
	s.storedCrc = safeReadU32(d, 16)

	// ---- Section 边界校验（Fail-Closed）----
	// 头部里的 section 偏移全部来自文件、可被伪造。若不在此处一次性验证，
	// 越界只会在查询期 trie 游走时才暴露成 panic，直接击穿调用方进程。
	// 这里提前把「偏移 + 该 section 最小必需长度」与文件长度比对，
	// 校验通过后查询热路径即可无判界地直接寻址。
	dataLen := uint64(len(d))
	checkOffset := func(offset, required uint64, field string) error {
		if offset > math.MaxUint64-required { // 加法回绕防护
			return newErr(ErrCodeOutOfBounds, fmt.Sprintf("offset overflow at %s", field))
		}
		if end := offset + required; end > dataLen {
			return newErr(ErrCodeOutOfBounds,
				fmt.Sprintf("section %s out of bounds (need %d, have %d)", field, end, dataLen))
		}
		return nil
	}
	v4NodeSize, v6NodeSize := uint64(8), uint64(8)
	if s.v4Node24 {
		v4NodeSize = 6
	}
	if s.v6Node24 {
		v6NodeSize = 6
	}
	for _, c := range []struct {
		off, need uint64
		name      string
		optional  bool
	}{
		{s.offV4Jump, 65536 * 4, "off_v4_jump", false},
		{s.offV4Nodes, uint64(s.v4NodeCount) * v4NodeSize, "off_v4_nodes", false},
		{s.offV6Jump, uint64(1)<<uint(s.v6JumpBits) * 4, "off_v6_jump", false},
		{s.offV6Nodes, uint64(s.v6NodeCount) * v6NodeSize, "off_v6_nodes", false},
		{s.offIPRow, uint64(s.rowCount) * uint64(s.ipRowSize), "off_ip_row", false},
		{s.offGeoEntries, 16, "off_geo_entries", true},
		{s.offPools, 4, "off_pools", true},
		{s.offMeta, 4, "off_meta", true},
		{s.offGroupSchema, 2, "off_group_schema", true},
		{s.offRowSchema, 1, "off_row_schema", true},
	} {
		if c.optional && c.off == 0 {
			continue
		}
		if err := checkOffset(c.off, c.need, c.name); err != nil {
			return err
		}
	}
	return nil
}

func (s *Snapshot) parseRowSchema() error {
	s.rowGeoWidth, s.rowAsnWidth, s.rowUsageWidth = 3, 3, 0
	if s.offRowSchema <= 0 {
		return nil
	}
	d := s.data
	sp := s.offRowSchema
	if sp+4+uint64(8)*4 > uint64(len(d)) {
		return nil
	}
	fCount := int(d[sp])
	stride := int(d[sp+1])
	if fCount < 1 || fCount > 8 || stride != s.ipRowSize {
		return nil
	}
	geoW, asnW, usageW, total := 0, 0, 0, 0
	ok := true
	wpos := sp + 4
	for i := 0; i < fCount; i++ {
		fid := int(d[wpos])
		w := int(d[wpos+1])
		switch fid {
		case 0:
			geoW = w
		case 1:
			asnW = w
		case 2:
			usageW = w
		}
		wpos += 4
		total += w
		if w < 1 || w > 4 {
			ok = false
		}
	}
	if ok && total == s.ipRowSize {
		s.rowGeoWidth, s.rowAsnWidth, s.rowUsageWidth = geoW, asnW, usageW
	}
	return nil
}

func (s *Snapshot) parseGroups() error {
	d := s.data
	headerGeoOffsets := [4]uint64{}
	for i := 0; i < 4; i++ {
		headerGeoOffsets[i] = s.readU48(uint64(168 + i*6))
	}
	if s.offGeoEntries <= 0 {
		return newErr(ErrCodeCorrupted, "missing geo_entries section")
	}
	gmOff := s.offGeoEntries
	if gmOff+1 > uint64(len(d)) {
		return newErr(ErrCodeCorrupted, "group metadata table out of bounds")
	}
	tableGroups := int(d[gmOff])
	gmOff++
	groups := tableGroups
	if s.geoEntryGroupCount < groups {
		groups = s.geoEntryGroupCount
	}
	if groups > 4 {
		groups = 4
	}
	if groups < 1 {
		return newErr(ErrCodeCorrupted, "group metadata table groupCount is 0")
	}
	if gmOff+uint64(groups)*7 > uint64(len(d)) {
		return newErr(ErrCodeCorrupted, "group metadata table truncated")
	}
	s.actualGroups = groups
	s.groupFieldCounts = make([]int, groups)
	s.groupEntryCounts = make([]uint32, groups)
	s.groupDimMasks = make([]uint16, groups)
	s.groupEntryOffsets = make([]uint64, groups)
	s.groupStrides = make([]int, groups)
	s.groupFieldWidths = make([][]int, groups)
	s.groupFieldOffsets = make([][]int, groups)
	s.groupFieldNative = make([][]bool, groups)
	s.groupFieldNativeType = make([][]int, groups)
	s.groupFieldIds = make([][]uint16, groups)
	s.groupIds = make([]uint16, groups)

	for gi := 0; gi < groups; gi++ {
		s.groupFieldCounts[gi] = int(d[gmOff])
		gmOff++
		s.groupEntryCounts[gi] = safeReadU32(d, gmOff)
		gmOff += 4
		s.groupDimMasks[gi] = safeReadU16(d, gmOff)
		gmOff += 2
		s.groupEntryOffsets[gi] = s.offGeoEntries + headerGeoOffsets[gi]
	}
	if s.groupIndex < 0 || s.groupIndex >= groups {
		return newErr(ErrCodeInvalidParam,
			fmt.Sprintf("groupIndex out of range [0,%d]: %d", groups-1, s.groupIndex))
	}

	if s.offGroupSchema > 0 && s.offGroupSchema+2 <= uint64(len(d)) {
		sp := s.offGroupSchema
		gsGroupCount := int(safeReadU16(d, sp))
		sp += 2
		maxGs := gsGroupCount
		if groups < maxGs {
			maxGs = groups
		}
		for gi := 0; gi < maxGs; gi++ {
			if sp+14 > uint64(len(d)) {
				break
			}
			// groupId 就是该组的 one-hot 版本位掩码，与 Header.VersionMask 同一套编码
			s.groupIds[gi] = safeReadU16(d, sp)
			sp += 2
			fldCount := int(safeReadU16(d, sp))
			sp += 2
			sp += 4 // entryCount
			stride := int(safeReadU32(d, sp))
			sp += 4
			sp += 4 // flags
			if fldCount < 0 || fldCount > 255 || sp+uint64(fldCount)*12 > uint64(len(d)) {
				break
			}
			s.groupStrides[gi] = stride
			widths := make([]int, fldCount)
			offsets := make([]int, fldCount)
			natives := make([]bool, fldCount)
			natTypes := make([]int, fldCount)
			fids := make([]uint16, fldCount)
			for fi := 0; fi < fldCount; fi++ {
				fids[fi] = safeReadU16(d, sp)
				sp += 2
				widths[fi] = int(d[sp])
				sp++
				ff := d[sp]
				sp++
				natives[fi] = ff&0x01 != 0
				natTypes[fi] = int((ff >> 1) & 0x03)
				offsets[fi] = int(safeReadU32(d, sp))
				sp += 4
				sp += 4 // poolSectionId
			}
			s.groupFieldWidths[gi] = widths
			s.groupFieldOffsets[gi] = offsets
			s.groupFieldNative[gi] = natives
			s.groupFieldNativeType[gi] = natTypes
			s.groupFieldIds[gi] = fids
		}
	}
	for g := 0; g < groups; g++ {
		if s.groupStrides[g] == 0 {
			s.groupStrides[g] = s.groupFieldCounts[g] * s.poolIdxSize
		}
		if s.groupFieldWidths[g] == nil {
			s.groupFieldWidths[g] = make([]int, s.groupFieldCounts[g])
			s.groupFieldOffsets[g] = make([]int, s.groupFieldCounts[g])
			for i := range s.groupFieldWidths[g] {
				s.groupFieldWidths[g][i] = s.poolIdxSize
				s.groupFieldOffsets[g][i] = i * s.poolIdxSize
			}
		}
		if s.groupFieldNative[g] == nil {
			s.groupFieldNative[g] = make([]bool, s.groupFieldCounts[g])
		}
		if s.groupFieldNativeType[g] == nil {
			s.groupFieldNativeType[g] = make([]int, s.groupFieldCounts[g])
		}
	}
	// 关键不变量：字段数有两个来源——GEO_ENTRIES 表的 groupFieldCounts[g]
	// 与 GROUP_SCHEMA 的 fldCount。畸形文件可让二者不一致，而查询热路径以
	// groupFieldCounts[g] 为循环上界直接下标访问 widths/offsets/...，长度不足
	// 即 index out of range panic（且不在 buildSnapshot 的 recover 范围内）。
	// 这里在解析期一次性对齐：不一致则该组回退到 poolIdxSize 默认布局，
	// 使 len(...) == groupFieldCounts[g] 恒成立，热路径无需逐次判界。
	for g := 0; g < groups; g++ {
		fc := s.groupFieldCounts[g]
		if len(s.groupFieldWidths[g]) == fc && len(s.groupFieldOffsets[g]) == fc &&
			len(s.groupFieldNative[g]) == fc && len(s.groupFieldNativeType[g]) == fc {
			continue
		}
		s.groupStrides[g] = fc * s.poolIdxSize
		widths := make([]int, fc)
		offsets := make([]int, fc)
		for i := 0; i < fc; i++ {
			widths[i] = s.poolIdxSize
			offsets[i] = i * s.poolIdxSize
		}
		s.groupFieldWidths[g] = widths
		s.groupFieldOffsets[g] = offsets
		s.groupFieldNative[g] = make([]bool, fc)
		s.groupFieldNativeType[g] = make([]int, fc)
	}
	return nil
}

// parseMetadata 读取 Metadata TLV 与字段名、版本、edition、构建日期推算。
func (s *Snapshot) parseMetadata() error {
	d := s.data
	var metaVersion, metaDesc, metaPrimary string
	var metaFields []string
	if s.flags&4 != 0 && s.offMeta > 0 && s.offMeta+4 <= uint64(len(d)) {
		cursor := s.offMeta
		size := uint64(len(d))
		for cursor+4 <= size {
			t := d[cursor]
			length := uint64(safeReadU16(d, cursor+2))
			if t == 0 || length == 0 {
				break
			}
			// 伪造的 TLV 可以声明一个越过 EOF 的长度；直接停止遍历
			if length > size-(cursor+4) {
				break
			}
			val := string(d[cursor+4 : cursor+4+length])
			switch t {
			case 1:
				metaVersion = val
			case 2:
				metaFields = splitFieldNames(val)
			case 3:
				metaDesc = val
			case 4:
				metaPrimary = val
			}
			cursor += 4 + length
		}
	}
	s.version = metaVersion
	s.description = metaDesc

	gi := s.groupIndex
	if gi < 0 || gi >= len(s.groupFieldCounts) {
		gi = 0
	}
	numFields := s.groupFieldCounts[gi]

	// --- edition：先用本组自己的掩码，再回落到文件级掩码 -----------------------
	mask := s.versionMask
	if gi < len(s.groupIds) && s.groupIds[gi] != 0 {
		mask = s.groupIds[gi]
	}
	edition := EditionFromMask(mask)
	editionSource := EditionSourceVersionMask
	if edition == "" {
		edition = strings.TrimSpace(metaPrimary)
		if edition == "" {
			var tokens []string
			for _, tok := range strings.Split(metaVersion, ",") {
				if tok = strings.TrimSpace(tok); tok != "" {
					tokens = append(tokens, tok)
				}
			}
			if len(tokens) == 1 {
				edition = tokens[0]
			}
		}
		if edition != "" {
			editionSource = EditionSourceMetadata
		}
	}
	if edition == "" {
		if edition = editionByFieldCount[numFields]; edition != "" {
			editionSource = EditionSourceInferred
		} else {
			editionSource = EditionSourceUnknown
		}
	}
	s.edition = edition
	s.editionSource = editionSource

	// --- 字段名 ---------------------------------------------------------------
	canonical := EditionFieldNames[edition]
	switch {
	case metaFields != nil && len(metaFields) == numFields:
		s.fieldNames = metaFields
		s.fieldNamesSource = FieldNamesSourceMetadata
	case canonical != nil && len(canonical) == numFields:
		s.fieldNames = append([]string(nil), canonical...)
		s.fieldNamesSource = FieldNamesSourceEdition
	default:
		names := make([]string, numFields)
		for i := range names {
			names[i] = fmt.Sprintf("field_%d", i)
		}
		s.fieldNames = names
		s.fieldNamesSource = FieldNamesSourceSynthetic
	}
	s.normalizedMap = buildNormalizedMap(s.fieldNames)
	s.numericFlags = make([]bool, len(s.fieldNames))
	for i, n := range s.fieldNames {
		s.numericFlags[i] = isNumericFieldName(n)
	}

	// 维度掩码兜底：只看解析出来的字段名里有没有 asn。fieldId 只是槽位序号
	// （0..N-1），不带任何跨档语义，绝不可用来判定维度。
	_, hasAsn := s.normalizedMap["asn"]
	for g := 0; g < s.actualGroups; g++ {
		if s.groupDimMasks[g] != 0 {
			continue
		}
		if hasAsn {
			s.groupDimMasks[g] = 0x02
		} else {
			s.groupDimMasks[g] = 0x01
		}
	}

	// 构建日期（Header 偏移 32：yyyyMMdd）推算
	buildDate := safeReadU32(d, 32)
	if buildDate > 0 {
		y := buildDate / 10000
		m := (buildDate / 100) % 100
		dd := buildDate % 100
		s.dataMonth = fmt.Sprintf("%04d-%02d", y, m)
		s.buildTimeStr = fmt.Sprintf("%04d-%02d-%02d", y, m, dd)
	}
	s.scope = ""
	return nil
}

func splitFieldNames(raw string) []string {
	s := strings.TrimSpace(raw)
	if s == "" {
		return nil
	}
	parts := strings.Split(s, "|")
	if len(parts) == 1 {
		parts = strings.Split(s, ",")
	}
	for i := range parts {
		parts[i] = strings.TrimSpace(parts[i])
	}
	return parts
}

func (s *Snapshot) parsePools() error {
	groupCount := s.actualGroups
	s.groupPools = make([][][]string, groupCount)
	if s.offPools <= 0 {
		for g := 0; g < groupCount; g++ {
			fc := s.groupFieldCounts[g]
			s.groupPools[g] = make([][]string, fc)
			for f := 0; f < fc; f++ {
				s.groupPools[g][f] = []string{}
			}
		}
		return nil
	}
	d := s.data
	poolCursor := s.offPools
	poolEnd := s.offMeta
	if poolEnd <= 0 {
		poolEnd = uint64(len(d))
	}
	const maxPoolCount = 1 << 24
	for g := 0; g < groupCount; g++ {
		fc := s.groupFieldCounts[g]
		list := make([][]string, fc)
		natives := s.groupFieldNative[g]
		for f := 0; f < fc; f++ {
			if natives != nil && f < len(natives) && natives[f] {
				list[f] = []string{}
				continue
			}
			if poolCursor+4 > poolEnd {
				list[f] = []string{}
				continue
			}
			count := safeReadU32(d, poolCursor)
			poolCursor += 4
			if s.offRowSchema > 0 {
				poolCursor += 4
			}
			if count == 0 || count > maxPoolCount {
				list[f] = []string{}
				continue
			}
			if poolCursor+uint64(count+1)*4 > poolEnd {
				list[f] = []string{}
				continue
			}
			offsets := make([]uint32, count+1)
			for o := range offsets {
				offsets[o] = safeReadU32(d, poolCursor)
				poolCursor += 4
			}
			// 偏移表是累积结构：offsets[i+1] >= offsets[i]，末项为字符串区总字节数。
			// 单调性必须强制校验 —— 仅判断 segEnd <= len(d) 时，伪造表可让每一项都横跨
			// 整个 section，count 段 × section 长度会放大成 GB 级 string 拷贝（实测同类
			// 构造在 Java 上达 7.2 GB → OOM）。加上 start >= prevEnd && end <= tail 后，
			// 各段互不重叠且落在 [0, tail]，总拷贝量必 <= tail <= avail。
			strBase := poolCursor
			if poolEnd > uint64(len(d)) {
				poolEnd = uint64(len(d))
			}
			if strBase > poolEnd {
				list[f] = []string{}
				continue
			}
			avail := poolEnd - strBase
			tail := uint64(offsets[count])
			if tail > avail {
				list[f] = []string{}
				continue
			}
			strs := make([]string, count)
			var prevEnd uint32
			for idx := uint32(0); idx < count; idx++ {
				start := offsets[idx]
				end := offsets[idx+1]
				if start < prevEnd || end < start || uint64(end) > tail {
					continue
				}
				prevEnd = end
				if end > start {
					strs[idx] = string(d[strBase+uint64(start) : strBase+uint64(end)])
				}
			}
			poolCursor = strBase + tail
			list[f] = strs
		}
		s.groupPools[g] = list
	}
	return nil
}

// ---------- CRC32（canonical：偏移 16~19 填 0） ----------

func (s *Snapshot) computeCanonicalCrc() uint32 {
	d := s.data
	crc := crc32.Update(0, crc32.IEEETable, d[:16])
	crc = crc32.Update(crc, crc32.IEEETable, []byte{0, 0, 0, 0})
	crc = crc32.Update(crc, crc32.IEEETable, d[20:])
	return crc
}

func (s *Snapshot) verifyCrcNow() bool {
	return s.computeCanonicalCrc() == s.storedCrc
}

func (s *Snapshot) fileHashHex() string {
	s.crcOnce.Do(func() { s.canonicalCrc = s.computeCanonicalCrc() })
	return fmt.Sprintf("%08x", s.canonicalCrc)
}

// ---------- Trie 遍历（返回已剥离哨兵位的 row_id） ----------

func (s *Snapshot) readV4Child(idx uint32, bit uint32) uint32 {
	if idx >= s.v4NodeCount {
		return 0
	}
	d := s.data
	if s.v4Node24 {
		off := s.offV4Nodes + uint64(idx)*6 + uint64(bit)*3
		return uint32(d[off]) | uint32(d[off+1])<<8 | uint32(d[off+2])<<16
	}
	off := s.offV4Nodes + uint64(idx)*8 + uint64(bit)*4
	return safeReadU32(d, off)
}

func (s *Snapshot) readV6Child(idx uint32, bit uint32) uint32 {
	if idx >= s.v6NodeCount {
		return 0
	}
	d := s.data
	if s.v6Node24 {
		off := s.offV6Nodes + uint64(idx)*6 + uint64(bit)*3
		return uint32(d[off]) | uint32(d[off+1])<<8 | uint32(d[off+2])<<16
	}
	off := s.offV6Nodes + uint64(idx)*8 + uint64(bit)*4
	return safeReadU32(d, off)
}

// use24BitNode 判断当前 IP 版本是否使用 24 位紧凑节点。
func (s *Snapshot) use24BitNode(v4 bool) bool {
	return (s.v4Node24 && v4) || (s.v6Node24 && !v4)
}

// nodeMask 返回当前节点类型的哨兵剥离掩码。
func (s *Snapshot) nodeMask(v4 bool) uint32 {
	if s.use24BitNode(v4) {
		return SENTINEL_MASK_24
	}
	return SENTINEL_MASK_31
}

// isLeaf 判断子节点指针是否为叶子（携带哨兵位）。
func (s *Snapshot) isLeaf(child uint32, v4 bool) bool {
	if s.use24BitNode(v4) {
		return child&0x800000 != 0
	}
	return child&SENTINEL != 0
}

// leafValue 从叶子指针提取 row_id。
func (s *Snapshot) leafValue(child uint32, v4 bool) uint32 {
	if s.use24BitNode(v4) {
		return child & SENTINEL_MASK_24
	}
	return child & SENTINEL_MASK_31
}

func (s *Snapshot) trieWalkV4(ip uint32) (uint32, error) {
	if !s.hasV4 || s.offV4Jump <= 0 {
		return 0, nil
	}
	ptr := safeReadU32(s.data, s.offV4Jump+uint64(ip>>16)*4)
	if ptr == 0 {
		return 0, nil
	}
	if ptr&SENTINEL != 0 {
		return ptr & SENTINEL_MASK_31, nil
	}
	idx := ptr & SENTINEL_MASK_31
	suffix := (ip & 0xFFFF) << 16
	mask := s.nodeMask(true)
	for steps := 0; steps < maxTrieWalkSteps; steps++ {
		child := s.readV4Child(idx, (suffix>>31)&1)
		if child == 0 {
			return 0, nil
		}
		if s.isLeaf(child, true) {
			return s.leafValue(child, true), nil
		}
		idx = child & mask
		suffix <<= 1
	}
	return 0, ErrCorrupted
}

func (s *Snapshot) trieWalkV6(ip [16]byte) (uint32, error) {
	if !s.hasV6 || s.offV6Jump <= 0 {
		return 0, nil
	}
	ptr := safeReadU32(s.data, s.offV6Jump+uint64(readV6Prefix(ip, s.v6JumpBits))*4)
	if ptr == 0 {
		return 0, nil
	}
	if ptr&SENTINEL != 0 {
		return ptr & SENTINEL_MASK_31, nil
	}
	idx := ptr & SENTINEL_MASK_31
	mask := s.nodeMask(false)
	for depth := s.v6JumpBits; depth < 128; depth++ {
		child := s.readV6Child(idx, uint32((ip[depth>>3]>>(7-uint(depth&7)))&1))
		if child == 0 {
			return 0, nil
		}
		if s.isLeaf(child, false) {
			return s.leafValue(child, false), nil
		}
		idx = child & mask
	}
	return 0, nil
}

// readV6Prefix 提取 IPv6 地址高 bits 位作为跳表索引。
func readV6Prefix(ip [16]byte, bits int) int {
	val := 0
	for i := 0; i < bits; i++ {
		val = (val << 1) | int((ip[i>>3]>>(7-uint(i&7)))&1)
	}
	return val
}

// ---------- IPRow / GeoEntry 解析 ----------

func (s *Snapshot) readIPRow(rowID uint32) (uint32, uint32, uint32) {
	if rowID == 0 || int(rowID) >= s.rowCount {
		return 0, 0, 0
	}
	off := s.offIPRow + uint64(rowID)*uint64(s.ipRowSize)
	if s.offRowSchema > 0 {
		geoID := s.readUintWidth(off, s.rowGeoWidth)
		asnID := uint32(0)
		usageID := uint32(0)
		p := off + uint64(s.rowGeoWidth)
		if s.rowAsnWidth > 0 {
			asnID = s.readUintWidth(p, s.rowAsnWidth)
			p += uint64(s.rowAsnWidth)
		}
		if s.rowUsageWidth > 0 {
			usageID = s.readUintWidth(p, s.rowUsageWidth)
		}
		return geoID, asnID, usageID
	}
	geoID := s.readU24(off)
	asnID := s.readU24(off + 3)
	usageID := uint32(0)
	if s.ipRowSize >= 9 {
		usageID = s.readU24(off + 6)
	}
	return geoID, asnID, usageID
}

// extractGeoInfo 解包一行 GeoEntry（全集），含 per-snapshot 无锁缓存。
func (s *Snapshot) extractGeoInfo(rowID uint32) *GeoInfo {
	if rowID == 0 {
		return nil
	}
	if g := s.geoCache.get(rowID); g != nil {
		return g
	}
	g := s.computeGeoInfo(rowID)
	if g != nil {
		s.geoCache.put(rowID, g)
	}
	return g
}

// resolveEntry 根据 row_id 解析 entryID 与 entry 字节偏移。
func (s *Snapshot) resolveEntry(rowID uint32) (entryID uint32, entryOff uint64, fc int, ok bool) {
	if rowID == 0 {
		return 0, 0, 0, false
	}
	geoID, asnID, usageID := s.readIPRow(rowID)
	gi := s.groupIndex
	switch s.groupDimMasks[gi] & 0x06 {
	case 0x02:
		entryID = asnID
	case 0x04:
		entryID = usageID
	default:
		entryID = geoID
	}
	if entryID == 0 || entryID >= s.groupEntryCounts[gi] {
		return 0, 0, 0, false
	}
	fc = s.groupFieldCounts[gi]
	entryOff = s.groupEntryOffsets[gi] + uint64(entryID)*uint64(s.groupStrides[gi])
	if entryOff+uint64(s.groupStrides[gi]) > uint64(len(s.data)) {
		return 0, 0, 0, false
	}
	return entryID, entryOff, fc, true
}

// readFieldValue 读取单个字段的值（支持原生标量与池索引）。
func (s *Snapshot) readFieldValue(entryOff uint64, fi int) string {
	fo := entryOff + uint64(s.groupFieldOffsets[s.groupIndex][fi])
	w := s.groupFieldWidths[s.groupIndex][fi]
	natives := s.groupFieldNative[s.groupIndex]
	if natives != nil && fi < len(natives) && natives[fi] {
		nt := 0
		if natTypes := s.groupFieldNativeType[s.groupIndex]; natTypes != nil && fi < len(natTypes) {
			nt = natTypes[fi]
		}
		return s.readNativeValue(fo, w, nt)
	}
	idx := s.readUintWidth(fo, w)
	pool := s.groupPools[s.groupIndex]
	if pool != nil && fi < len(pool) && int(idx) < len(pool[fi]) {
		return pool[fi][idx]
	}
	return ""
}

func (s *Snapshot) computeGeoInfo(rowID uint32) *GeoInfo {
	_, entryOff, fc, ok := s.resolveEntry(rowID)
	if !ok {
		return nil
	}
	values := make([]string, fc)
	for i := 0; i < fc; i++ {
		values[i] = s.readFieldValue(entryOff, i)
	}
	return &GeoInfo{
		FieldNames: s.fieldNames,
		Values:     values,
		normMap:    s.normalizedMap,
		numeric:    s.numericFlags,
	}
}

// computeGeoInfoProjected 只读取 fields 指定的字段（按请求顺序），避免全字段解析。
func (s *Snapshot) computeGeoInfoProjected(rowID uint32, fields []string) *GeoInfo {
	_, entryOff, fc, ok := s.resolveEntry(rowID)
	if !ok {
		return nil
	}
	values := make([]string, len(fields))
	for i, f := range fields {
		origIdx, found := s.normalizedMap[normalizeKey(f)]
		if !found || origIdx >= fc {
			continue
		}
		values[i] = s.readFieldValue(entryOff, origIdx)
	}
	return &GeoInfo{
		FieldNames: fields,
		Values:     values,
		normMap:    buildNormalizedMap(fields),
	}
}

// readNativeValue 原生标量字段解码：int 原样；float 按 6 位小数（NaN/Inf → ""，整数值 → 无小数点）。
func (s *Snapshot) readNativeValue(off uint64, w int, nt int) string {
	if int(off)+w > len(s.data) {
		return ""
	}
	if nt == 1 {
		if w == 4 {
			f := float64(math.Float32frombits(safeReadU32(s.data, off)))
			return formatFloat6(f)
		}
		f := math.Float64frombits(safeReadU64(s.data, off))
		return formatFloat6(f)
	}
	valNum := s.readUintWidth(off, w)
	return strconv.FormatUint(uint64(valNum), 10)
}

// formatFloat6 原生浮点格式化：整数值无小数点；否则固定 6 位小数；NaN/Inf 返回 ""。
func formatFloat6(f float64) string {
	if math.IsNaN(f) || math.IsInf(f, 0) {
		return ""
	}
	if f == math.Trunc(f) {
		return strconv.FormatInt(int64(f), 10)
	}
	return strconv.FormatFloat(f, 'f', 6, 64)
}

// ---------- 查询入口 ----------

func (r *QzdbReader) snapshot() *Snapshot {
	for {
		s := r.snap.Load()
		if s == nil {
			return nil
		}
		s.refCount.Add(1)
		if r.snap.Load() == s {
			return s
		}
		s.refCount.Add(-1)
	}
}

func (s *Snapshot) releaseSnapshot() {
	if s.refCount.Add(-1) == 0 {
		if s.release != nil {
			s.release()
		}
	}
}

// Find 查询 IP 字符串；未命中或非法 IP 返回 (nil, nil)（契约 §4）。
func (r *QzdbReader) Find(ipStr string) (*GeoInfo, error) {
	s := r.snapshot()
	if s == nil {
		return nil, ErrClosed
	}
	defer s.releaseSnapshot()
	if ipStr == "" {
		return nil, nil
	}
	res, ok := fastParseIp(ipStr)
	if !ok {
		return nil, nil
	}
	if res.isV4 {
		return r.findUint(s, res.v4)
	}
	return r.findV6(s, res.v6)
}

// FindUint 查询 IPv4 uint32。
func (r *QzdbReader) FindUint(ipInt uint32) (*GeoInfo, error) {
	s := r.snapshot()
	if s == nil {
		return nil, ErrClosed
	}
	defer s.releaseSnapshot()
	return r.findUint(s, ipInt)
}

// FindV6Uint 查询 16 字节 IPv6 地址。
func (r *QzdbReader) FindV6Uint(ip16 [16]byte) (*GeoInfo, error) {
	s := r.snapshot()
	if s == nil {
		return nil, ErrClosed
	}
	defer s.releaseSnapshot()
	return r.findV6(s, ip16)
}

// FindBytes 查询 16 字节地址（IPv4-mapped 自动降级）。
func (r *QzdbReader) FindBytes(ip16 [16]byte) (*GeoInfo, error) {
	s := r.snapshot()
	if s == nil {
		return nil, ErrClosed
	}
	defer s.releaseSnapshot()
	if isV4Mapped(ip16) {
		return r.findUint(s, v4FromMapped(ip16))
	}
	return r.findV6(s, ip16)
}

// FindAddr 查询 netip.Addr（Go 原生 IP 类型重载，契约 §5.2）。
// IPv4-mapped IPv6 地址自动降级走 V4 Trie。
func (r *QzdbReader) FindAddr(addr netip.Addr) (*GeoInfo, error) {
	if !addr.IsValid() {
		return nil, nil
	}
	if addr.Is4() {
		a4 := addr.As4()
		return r.FindUint(uint32(a4[0])<<24 | uint32(a4[1])<<16 | uint32(a4[2])<<8 | uint32(a4[3]))
	}
	if addr.Is4In6() {
		a4 := addr.As4()
		return r.FindUint(uint32(a4[0])<<24 | uint32(a4[1])<<16 | uint32(a4[2])<<8 | uint32(a4[3]))
	}
	ip16 := addr.As16()
	return r.FindBytes(ip16)
}

func (r *QzdbReader) findUint(s *Snapshot, ip uint32) (*GeoInfo, error) {
	if !s.hasV4 {
		return nil, nil
	}
	rowID, err := s.trieWalkV4(ip)
	if err != nil {
		return nil, err
	}
	if rowID == 0 {
		return nil, nil
	}
	g := s.extractGeoInfo(rowID)
	if g == nil {
		return nil, nil
	}
	return g, nil
}

func (r *QzdbReader) findV6(s *Snapshot, ip [16]byte) (*GeoInfo, error) {
	if !s.hasV6 {
		return nil, nil
	}
	rowID, err := s.trieWalkV6(ip)
	if err != nil {
		return nil, err
	}
	if rowID == 0 {
		return nil, nil
	}
	g := s.extractGeoInfo(rowID)
	if g == nil {
		return nil, nil
	}
	return g, nil
}

// FindStr 返回 toPipe() 字符串；未命中/非法返回 ""。
func (r *QzdbReader) FindStr(ipStr string) string {
	g, _ := r.Find(ipStr)
	if g == nil {
		return ""
	}
	return g.ToPipe()
}

// FindFields 字段投影查询：只解析 fields 指定的字段（对标 Java §9.6 投影模式）。
// fields 为空等价于 Find。只读取需要的字段，避免全字段解析的分配开销。
func (r *QzdbReader) FindFields(ipStr string, fields []string) (*GeoInfo, error) {
	s := r.snapshot()
	if s == nil {
		return nil, ErrClosed
	}
	defer s.releaseSnapshot()
	if len(fields) == 0 {
		return r.Find(ipStr)
	}
	if ipStr == "" {
		return nil, nil
	}
	res, ok := fastParseIp(ipStr)
	if !ok {
		return nil, nil
	}
	var rowID uint32
	var err error
	if res.isV4 {
		rowID, err = s.trieWalkV4(res.v4)
	} else {
		rowID, err = s.trieWalkV6(res.v6)
	}
	if err != nil {
		return nil, err
	}
	if rowID == 0 {
		return nil, nil
	}
	return s.computeGeoInfoProjected(rowID, fields), nil
}

// ---------- 低级行号 ----------

// LookupRowId 返回 row_id（0 表示未命中/非法）。
func (r *QzdbReader) LookupRowId(ipStr string) uint32 {
	s := r.snapshot()
	if s == nil {
		return 0
	}
	defer s.releaseSnapshot()
	if ipStr == "" {
		return 0
	}
	res, ok := fastParseIp(ipStr)
	if !ok {
		return 0
	}
	if res.isV4 {
		rowID, _ := s.trieWalkV4(res.v4)
		return rowID
	}
	rowID, _ := s.trieWalkV6(res.v6)
	return rowID
}

// LookupRowIdUint 返回 IPv4 uint32 的 row_id。
func (r *QzdbReader) LookupRowIdUint(ipInt uint32) uint32 {
	s := r.snapshot()
	if s == nil || !s.hasV4 {
		return 0
	}
	defer s.releaseSnapshot()
	rowID, _ := s.trieWalkV4(ipInt)
	return rowID
}

// LookupRowIdV6 返回 16 字节 IPv6 的 row_id。
func (r *QzdbReader) LookupRowIdV6(ip16 [16]byte) uint32 {
	s := r.snapshot()
	if s == nil || !s.hasV6 {
		return 0
	}
	defer s.releaseSnapshot()
	rowID, _ := s.trieWalkV6(ip16)
	return rowID
}

// LookupRowIdBytes 返回 4/16 字节地址的 row_id（长度非法返回 0）。
// 16 字节的 IPv4-mapped 地址（::ffff:a.b.c.d）自动降级为 V4 查询，与 FindBytes 语义一致。
func (r *QzdbReader) LookupRowIdBytes(ip []byte) uint32 {
	if len(ip) == 16 {
		var v6 [16]byte
		copy(v6[:], ip)
		if isV4Mapped(v6) {
			return r.LookupRowIdUint(v4FromMapped(v6))
		}
		return r.LookupRowIdV6(v6)
	}
	if len(ip) == 4 {
		return r.LookupRowIdUint(uint32(ip[0])<<24 | uint32(ip[1])<<16 | uint32(ip[2])<<8 | uint32(ip[3]))
	}
	return 0
}

// RowIds 是 row_id 三元组（geoId, asnId, usageId）。
type RowIds struct {
	GeoID   uint32
	AsnID   uint32
	UsageID uint32
}

// LookupIds 返回 row_id 对应各维度 ID；越界返回 nil。
func (r *QzdbReader) LookupIds(rowID uint32) *RowIds {
	s := r.snapshot()
	if s == nil || rowID == 0 || int(rowID) >= s.rowCount {
		return nil
	}
	defer s.releaseSnapshot()
	geoID, asnID, usageID := s.readIPRow(rowID)
	return &RowIds{GeoID: geoID, AsnID: asnID, UsageID: usageID}
}

// ---------- 元信息自省 ----------

// GetVersion 返回 Metadata 版本；无则返回 ""。
func (r *QzdbReader) GetVersion() string {
	if s := r.snapshot(); s != nil {
		defer s.releaseSnapshot()
		return s.version
	}
	return ""
}

// Version 是 GetVersion 的别名（兼容旧 API）。
func (r *QzdbReader) Version() string { return r.GetVersion() }

// GetDataMonth 返回数据期号 "yyyy-MM"。
func (r *QzdbReader) GetDataMonth() string {
	if s := r.snapshot(); s != nil {
		defer s.releaseSnapshot()
		return s.dataMonth
	}
	return ""
}

// GetEdition 返回版本档次（std/pro/asn/max/ult）。
func (r *QzdbReader) GetEdition() string {
	if s := r.snapshot(); s != nil {
		defer s.releaseSnapshot()
		return s.edition
	}
	return ""
}

// GetVersionMask 返回 Header.VersionMask（偏移 6）原值：one-hot 版本位掩码。
// bit0=std, bit1=asn, bit2=pro, bit3=max, bit4=ult（FORMAT §3.1）。
func (r *QzdbReader) GetVersionMask() uint16 {
	if s := r.snapshot(); s != nil {
		defer s.releaseSnapshot()
		return s.versionMask
	}
	return 0
}

// GetEditionSource 报告 GetEdition 命中了哪条规则：
// version_mask | metadata | inferred | unknown。
func (r *QzdbReader) GetEditionSource() string {
	if s := r.snapshot(); s != nil {
		defer s.releaseSnapshot()
		return s.editionSource
	}
	return EditionSourceUnknown
}

// GetFieldNamesSource 报告 GetFieldNames 的来源：metadata | edition | synthetic。
//
// metadata 表示字段名是从文件里读出来的；edition 表示文件没写、由 SDK 按其声明的
// 档次补上规范表；synthetic 表示两者都没有，名字只是位置占位符，按名取值无意义。
func (r *QzdbReader) GetFieldNamesSource() string {
	if s := r.snapshot(); s != nil {
		defer s.releaseSnapshot()
		return s.fieldNamesSource
	}
	return FieldNamesSourceSynthetic
}

// GetScope 始终返回 ""（当前格式 Header 尚无 scope 字段）。
func (r *QzdbReader) GetScope() string { return "" }

// GetBuildTime 返回构建日期 "yyyy-MM-dd"。
func (r *QzdbReader) GetBuildTime() string {
	if s := r.snapshot(); s != nil {
		defer s.releaseSnapshot()
		return s.buildTimeStr
	}
	return ""
}

// GetDescription 返回 Metadata 描述；无则返回 ""。
func (r *QzdbReader) GetDescription() string {
	if s := r.snapshot(); s != nil {
		defer s.releaseSnapshot()
		return s.description
	}
	return ""
}

// GetFileHash 返回文件 CRC32 十六进制字符串（8 位小写）。
func (r *QzdbReader) GetFileHash() string {
	if s := r.snapshot(); s != nil {
		defer s.releaseSnapshot()
		return s.fileHashHex()
	}
	return ""
}

// GetFieldNames 返回当前版本组字段名。
func (r *QzdbReader) GetFieldNames() []string {
	if s := r.snapshot(); s != nil {
		defer s.releaseSnapshot()
		out := make([]string, len(s.fieldNames))
		copy(out, s.fieldNames)
		return out
	}
	return nil
}

// FieldNames 是 GetFieldNames 的别名（兼容旧 API）。
func (r *QzdbReader) FieldNames() []string { return r.GetFieldNames() }

// HasField 判断当前版本组是否包含指定字段（大小写/下划线不敏感）。
func (r *QzdbReader) HasField(name string) bool {
	s := r.snapshot()
	if s == nil {
		return false
	}
	defer s.releaseSnapshot()
	_, ok := s.normalizedMap[normalizeKey(name)]
	return ok
}

// VerifyCRC 重新计算全文件 CRC32 并与存储值比对。
func (r *QzdbReader) VerifyCRC() bool {
	s := r.snapshot()
	if s == nil {
		return false
	}
	defer s.releaseSnapshot()
	return s.verifyCrcNow()
}

// GetGroupCount 返回版本组数量。
func (r *QzdbReader) GetGroupCount() int {
	if s := r.snapshot(); s != nil {
		defer s.releaseSnapshot()
		return s.actualGroups
	}
	return 0
}

// GetPoolCount 返回 Header poolCount。
func (r *QzdbReader) GetPoolCount() int {
	if s := r.snapshot(); s != nil {
		defer s.releaseSnapshot()
		return s.poolCount
	}
	return 0
}

// PoolCount 是 GetPoolCount 的别名（兼容旧 API）。
func (r *QzdbReader) PoolCount() int { return r.GetPoolCount() }

// GetGroupIndex 返回当前版本组索引。
func (r *QzdbReader) GetGroupIndex() int {
	if s := r.snapshot(); s != nil {
		defer s.releaseSnapshot()
		return s.groupIndex
	}
	return 0
}

// ---------- 加载 / 热更新 / 释放 ----------

// Open 便捷构造器：从文件路径打开一个 QzdbReader。verifyCrc=true 时强制校验。
// 需要更多选项时用 NewBuilder。
func Open(dbPath string, groupIndex int, verifyCrc bool) (*QzdbReader, error) {
	return NewBuilder(dbPath).GroupIndex(groupIndex).VerifyCRC(verifyCrc).Build()
}

// OpenBufferNoCopy 零拷贝变体：以内存字节加载 QzdbReader，不拷贝传入的 data。
// 调用方必须保证 data 在 QzdbReader 生命周期内只读且不被释放，否则行为未定义。
// 适用于 embed.FS / mmap 等常驻只读数据场景，避免大文件一次性内存翻倍。
func OpenBufferNoCopy(data []byte, groupIndex int, verifyCrc bool) (*QzdbReader, error) {
	return NewBuilderBytesNoCopy(data).GroupIndex(groupIndex).VerifyCRC(verifyCrc).Build()
}

// Close 释放 mmap / 内存引用；幂等；关闭后查询安全失败。
func (r *QzdbReader) Close() error {
	s := r.snap.Swap(nil)
	if s != nil {
		s.releaseSnapshot()
	}
	return nil
}

// Reload 原子热替换数据文件（强制 CRC，失败保留旧快照）。
func (r *QzdbReader) Reload(path string) error {
	cur := r.snapshot()
	if cur == nil {
		return newErr(ErrCodeInvalidParam, "reader is closed")
	}
	ns, err := buildSnapshotFromFile(path, cur.groupIndex, true)
	if err != nil {
		cur.releaseSnapshot()
		return err
	}
	ns.refCount.Add(1)
	old := r.snap.Swap(ns)
	cur.releaseSnapshot()
	if old != nil {
		old.releaseSnapshot()
	}
	return nil
}

// ReloadBuffer 原子热替换内存字节（强制 CRC，失败保留旧快照）。
func (r *QzdbReader) ReloadBuffer(b []byte) error {
	cur := r.snapshot()
	if cur == nil {
		return newErr(ErrCodeInvalidParam, "reader is closed")
	}
	ns, err := buildSnapshotFromBytes(b, cur.groupIndex, true)
	if err != nil {
		cur.releaseSnapshot()
		return err
	}
	ns.refCount.Add(1)
	old := r.snap.Swap(ns)
	cur.releaseSnapshot()
	if old != nil {
		old.releaseSnapshot()
	}
	return nil
}

// ---------- 文件 / 字节加载 ----------

func buildSnapshotFromFile(path string, groupIndex int, verifyCrc bool) (*Snapshot, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	fi, err := f.Stat()
	if err != nil {
		return nil, err
	}
	if fi.Size() < 192 {
		return nil, newErr(ErrCodeBadHeader, "file too small for QZDB header")
	}
	data, err := syscall.Mmap(int(f.Fd()), 0, int(fi.Size()), syscall.PROT_READ, syscall.MAP_PRIVATE)
	if err != nil {
		return nil, err
	}
	return buildSnapshot(data, func() { _ = syscall.Munmap(data) }, groupIndex, verifyCrc)
}

func buildSnapshotFromBytes(b []byte, groupIndex int, verifyCrc bool) (*Snapshot, error) {
	if len(b) < 192 {
		return nil, newErr(ErrCodeBadHeader, "buffer too small for QZDB header")
	}
	cp := make([]byte, len(b))
	copy(cp, b)
	return buildSnapshot(cp, nil, groupIndex, verifyCrc)
}

// 确保 errors 被引用（兼容旧代码可能的用法）。
var _ = errors.New
