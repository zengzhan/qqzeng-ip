package qzdb

import (
	"encoding/binary"
	"errors"
	"fmt"
	"hash/crc32"
	"math"
	"os"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"unsafe"
)

// Unified error codes for QZDB operations
var (
	ErrNotFound     = errors.New("not found")
	ErrCorrupted    = errors.New("corrupted data")
	ErrOutOfBounds  = errors.New("out of bounds")
	ErrInvalidParam = errors.New("invalid parameter")
	ErrBadHeader    = errors.New("bad header")
	ErrBadMagic     = errors.New("bad magic")
	ErrUnsupported  = errors.New("unsupported format")
)

const SENTINEL uint32 = 0x80000000
const SENTINEL_MASK_24 uint32 = 0x7FFFFF
const SENTINEL_MASK_31 uint32 = 0x7FFFFFFF
const maxTrieWalkSteps = 1000

var floatFields = map[string]bool{
	"longitude": true,
	"latitude":  true,
}

type GeoInfo struct {
	FieldNames   []string
	Values       []string
	floatIndices map[string]bool
}

func (g *GeoInfo) Get(name string) string {
	for i, n := range g.FieldNames {
		if n == name && i < len(g.Values) {
			return g.Values[i]
		}
	}
	return ""
}

func (g *GeoInfo) ToPipe() string {
	parts := make([]string, len(g.FieldNames))
	for i, fname := range g.FieldNames {
		val := ""
		if i < len(g.Values) {
			val = g.Values[i]
		}
		if g.floatIndices[fname] && val != "" {
			if f, err := strconv.ParseFloat(val, 64); err == nil {
				parts[i] = fmt.Sprintf("%.6f", f)
				continue
			}
		}
		parts[i] = val
	}
	return strings.Join(parts, "|")
}

func (g *GeoInfo) ToMap() map[string]string {
	m := make(map[string]string, len(g.FieldNames))
	for i, n := range g.FieldNames {
		if i < len(g.Values) {
			m[n] = g.Values[i]
		}
	}
	return m
}

type QzdbSearcher struct {
	data                []byte
	groupIndex          int
	fieldNames          []string
	fieldNameToIdx      map[string]int
	floatFieldIndices   map[string]bool
	versionName         string

	// Header fields
	flags               uint16
	hasV4               bool
	hasV6               bool
	v4Node24            bool
	v6Node24            bool
	v6JumpBits          int
	poolCount           int
	poolIdxSize         int
	geoCount            int
	rowCount            int
	v4RecCount          uint32
	v6RecCount          uint32
	v4NodeCount         uint32
	v6NodeCount         uint32
	ipRowSize           int
	geoEntryGroupCount  int

	// Offsets
	offV4Jump           uint64
	offV4Nodes          uint64
	offV6Jump           uint64
	offV6Nodes          uint64
	offIPRow            uint64
	offGeoEntries       uint64
	offPools            uint64
	offMeta             uint64
	offRowSchema        uint64
	offGroupSchema      uint64

	// Schema and layout cache
	groupFieldCounts    []int
	groupEntryCounts    []uint32
	groupDimMasks       []uint16
	groupEntryOffsets   []uint64

	groupStrides        []int
	groupFieldWidths    [][]int
	groupFieldOffsets   [][]int
	groupFieldNative    [][]bool
	groupFieldNativeType [][]int
	groupFieldIds       [][]uint16
	groupPoolSectionIds [][]uint32

	groupPools          [][][]string
	poolsLoaded         bool
}

var (
	instance *QzdbSearcher
	once     sync.Once
	initErr  error
)

func Instance(dbPath ...string) (*QzdbSearcher, error) {
	once.Do(func() {
		path := "qqzeng_ip_std_china.qzdb"
		if len(dbPath) > 0 {
			path = dbPath[0]
		}
		instance, initErr = NewSearcher(path, 0)
	})
	if initErr != nil {
		return nil, initErr
	}
	return instance, nil
}

func NewSearcher(dbPath string, groupIndex int) (*QzdbSearcher, error) {
	f, err := os.Open(dbPath)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	fi, err := f.Stat()
	if err != nil {
		return nil, err
	}
	if fi.Size() < 192 {
		return nil, fmt.Errorf("file too small for QZDB header")
	}
	data, err := syscall.Mmap(int(f.Fd()), 0, int(fi.Size()), syscall.PROT_READ, syscall.MAP_PRIVATE)
	if err != nil {
		return nil, err
	}
	s := &QzdbSearcher{data: data, groupIndex: groupIndex}
	if err := s.parseHeader(); err != nil {
		syscall.Munmap(data)
		return nil, err
	}
	s.ensurePoolsLoaded()
	return s, nil
}

func (s *QzdbSearcher) Close() {
	if s.data != nil {
		syscall.Munmap(s.data)
		s.data = nil
	}
}

//go:nosplit
func safeReadU16(p unsafe.Pointer) uint16 {
	b := (*[2]byte)(p)
	return uint16(b[0]) | uint16(b[1])<<8
}

//go:nosplit
func safeReadU32(p unsafe.Pointer) uint32 {
	b := (*[4]byte)(p)
	return uint32(b[0]) | uint32(b[1])<<8 | uint32(b[2])<<16 | uint32(b[3])<<24
}

//go:nosplit
func safeReadU64(p unsafe.Pointer) uint64 {
	b := (*[8]byte)(p)
	return uint64(b[0]) | uint64(b[1])<<8 | uint64(b[2])<<16 | uint64(b[3])<<24 |
		uint64(b[4])<<32 | uint64(b[5])<<40 | uint64(b[6])<<48 | uint64(b[7])<<56
}

func (s *QzdbSearcher) safeReadU24(off uint64) uint32 {
	d := s.data
	return uint32(d[off]) | uint32(d[off+1])<<8 | uint32(d[off+2])<<16
}

func (s *QzdbSearcher) safeReadU48(off uint64) uint64 {
	d := s.data
	return uint64(d[off]) | uint64(d[off+1])<<8 | uint64(d[off+2])<<16 |
		uint64(d[off+3])<<24 | uint64(d[off+4])<<32 | uint64(d[off+5])<<40
}

func (s *QzdbSearcher) safeReadUintWidth(off uint64, width int) uint32 {
	if width <= 1 {
		return uint32(s.data[off])
	} else if width == 2 {
		return uint32(safeReadU16(unsafe.Pointer(&s.data[off])))
	} else if width == 3 {
		return s.safeReadU24(off)
	} else {
		return safeReadU32(unsafe.Pointer(&s.data[off]))
	}
}

func (s *QzdbSearcher) parseHeader() error {
	d := s.data
	if len(d) < 192 {
		return fmt.Errorf("file too small for QZDB header")
	}

	magic := string(d[:4])
	if magic != "QZDB" {
		return fmt.Errorf("invalid magic, expected QZDB")
	}

	fmtVer := d[4]
	if fmtVer < 1 || fmtVer > 6 {
		return fmt.Errorf("unsupported format version: %d", fmtVer)
	}

	s.flags = safeReadU16(unsafe.Pointer(&d[8]))
	s.hasV4 = s.flags&1 != 0
	s.hasV6 = s.flags&2 != 0
	s.v4Node24 = s.flags&0x10 != 0
	s.v6Node24 = s.flags&0x20 != 0

	s.v6JumpBits = int(d[11])
	if s.v6JumpBits == 0 {
		s.v6JumpBits = 16
	}
	if s.v6JumpBits < 16 || s.v6JumpBits > 20 {
		return fmt.Errorf("v6JumpBits out of range [16,20]: %d", s.v6JumpBits)
	}

	s.poolCount = int(d[12])
	s.poolIdxSize = int(d[13])
	if s.poolIdxSize != 2 && s.poolIdxSize != 3 {
		return fmt.Errorf("poolIdxSize must be 2 or 3, got %d", s.poolIdxSize)
	}
	s.geoCount = int(safeReadU16(unsafe.Pointer(&d[14])))
	s.rowCount = int(safeReadU32(unsafe.Pointer(&d[20])))
	s.v4RecCount = safeReadU32(unsafe.Pointer(&d[24]))
	s.v6RecCount = safeReadU32(unsafe.Pointer(&d[28]))

	hs := safeReadU32(unsafe.Pointer(&d[36]))
	if hs != 192 {
		return fmt.Errorf("unexpected header size: %d", hs)
	}

	s.offRowSchema = safeReadU64(unsafe.Pointer(&d[40]))
	s.offGroupSchema = safeReadU64(unsafe.Pointer(&d[48]))
	s.offV4Jump = safeReadU64(unsafe.Pointer(&d[64]))
	s.offV4Nodes = safeReadU64(unsafe.Pointer(&d[72]))
	s.offV6Jump = safeReadU64(unsafe.Pointer(&d[80]))
	s.offV6Nodes = safeReadU64(unsafe.Pointer(&d[88]))
	s.offIPRow = safeReadU64(unsafe.Pointer(&d[96]))
	s.offGeoEntries = safeReadU64(unsafe.Pointer(&d[104]))
	s.offPools = safeReadU64(unsafe.Pointer(&d[136]))
	s.offMeta = safeReadU64(unsafe.Pointer(&d[144]))

	s.v4NodeCount = safeReadU32(unsafe.Pointer(&d[152]))
	s.v6NodeCount = safeReadU32(unsafe.Pointer(&d[156]))
	s.ipRowSize = int(safeReadU32(unsafe.Pointer(&d[160])))
	if s.ipRowSize < 1 || s.ipRowSize > 64 {
		return fmt.Errorf("ipRowSize out of range [1,64]: %d", s.ipRowSize)
	}
	s.geoEntryGroupCount = int(safeReadU32(unsafe.Pointer(&d[164])))
	if s.geoEntryGroupCount < 1 || s.geoEntryGroupCount > 255 {
		return fmt.Errorf("geoEntryGroupCount out of range [1,255]: %d", s.geoEntryGroupCount)
	}

	// Validate section offsets are within bounds
	if s.offV4Jump+65536*4 > uint64(len(d)) {
		return fmt.Errorf("V4 jump table offset out of bounds")
	}
	v4NodeSize := uint64(8)
	if s.v4Node24 {
		v4NodeSize = 6
	}
	if s.offV4Nodes+uint64(s.v4NodeCount)*v4NodeSize > uint64(len(d)) {
		return fmt.Errorf("V4 nodes table offset out of bounds")
	}
	v6JumpSize := uint64(1<<uint(s.v6JumpBits)) * 4
	if s.offV6Jump+v6JumpSize > uint64(len(d)) {
		return fmt.Errorf("V6 jump table offset out of bounds")
	}
	v6NodeSize := uint64(8)
	if s.v6Node24 {
		v6NodeSize = 6
	}
	if s.offV6Nodes+uint64(s.v6NodeCount)*v6NodeSize > uint64(len(d)) {
		return fmt.Errorf("V6 nodes table offset out of bounds")
	}
	if s.offIPRow+uint64(s.rowCount)*uint64(s.ipRowSize) > uint64(len(d)) {
		return fmt.Errorf("IP row table offset out of bounds")
	}
	if s.offGeoEntries > uint64(len(d)) {
		return fmt.Errorf("Geo entries offset out of bounds")
	}
	if s.offPools > uint64(len(d)) {
		return fmt.Errorf("Pools offset out of bounds")
	}
	if s.offMeta > uint64(len(d)) {
		return fmt.Errorf("Meta offset out of bounds")
	}

	s.groupEntryOffsets = make([]uint64, 4)
	for i := 0; i < 4; i++ {
		off := 168 + uint64(i)*6
		if off+6 > uint64(len(d)) {
			return fmt.Errorf("group entry offsets out of bounds")
		}
		s.groupEntryOffsets[i] = s.safeReadU48(off)
	}

	gmOff := s.offGeoEntries
	if gmOff >= uint64(len(d)) {
		return fmt.Errorf("geo entries offset out of bounds")
	}
	groupCount := int(d[gmOff])
	gmOff++

	actualGroups := groupCount
	if actualGroups < 1 {
		actualGroups = 1
	}
	if s.geoEntryGroupCount > 0 && s.geoEntryGroupCount < actualGroups {
		actualGroups = s.geoEntryGroupCount
	}
	if actualGroups > 4 {
		actualGroups = 4
	}

	s.groupFieldCounts = make([]int, actualGroups)
	s.groupEntryCounts = make([]uint32, actualGroups)
	s.groupDimMasks = make([]uint16, actualGroups)

	for gi := 0; gi < actualGroups; gi++ {
		s.groupFieldCounts[gi] = int(d[gmOff])
		gmOff++
		if fmtVer == 1 || fmtVer >= 4 {
			s.groupEntryCounts[gi] = safeReadU32(unsafe.Pointer(&d[gmOff]))
			gmOff += 4
		} else {
			s.groupEntryCounts[gi] = uint32(safeReadU16(unsafe.Pointer(&d[gmOff])))
			gmOff += 2
		}

		if fmtVer == 1 || fmtVer >= 3 {
			s.groupDimMasks[gi] = safeReadU16(unsafe.Pointer(&d[gmOff]))
			gmOff += 2
		} else {
			if gi != 2 {
				s.groupDimMasks[gi] = 0x01
			} else {
				s.groupDimMasks[gi] = 0x02
			}
		}
	}

	s.groupStrides = make([]int, actualGroups)
	s.groupFieldWidths = make([][]int, actualGroups)
	s.groupFieldOffsets = make([][]int, actualGroups)
	s.groupFieldNative = make([][]bool, actualGroups)
	s.groupFieldNativeType = make([][]int, actualGroups)
	s.groupFieldIds = make([][]uint16, actualGroups)
	s.groupPoolSectionIds = make([][]uint32, actualGroups)

	if s.offGroupSchema > 0 {
		sp := s.offGroupSchema
		gsGroupCount := int(safeReadU16(unsafe.Pointer(&d[sp])))
		sp += 2
		maxGsGroups := gsGroupCount
		if actualGroups < maxGsGroups {
			maxGsGroups = actualGroups
		}
		for gi := 0; gi < maxGsGroups; gi++ {
			sp += 2 // skip groupId
			fldCount := int(safeReadU16(unsafe.Pointer(&d[sp])))
			sp += 2
			sp += 4 // skip entryCount
			stride := int(safeReadU32(unsafe.Pointer(&d[sp])))
			sp += 4
			sp += 4 // skip flags

			if gi < actualGroups {
				s.groupStrides[gi] = stride
				widths := make([]int, fldCount)
				offsets := make([]int, fldCount)
				natives := make([]bool, fldCount)
				natTypes := make([]int, fldCount)
				fieldIds := make([]uint16, fldCount)
				poolSectionIds := make([]uint32, fldCount)
				for fi := 0; fi < fldCount; fi++ {
					fieldIds[fi] = safeReadU16(unsafe.Pointer(&d[sp]))
					sp += 2
					widths[fi] = int(d[sp])
					sp++
					fieldFlags := d[sp]
					sp++
					natives[fi] = (fieldFlags & 0x01) != 0
					natTypes[fi] = int((fieldFlags >> 1) & 0x03)
					offsets[fi] = int(safeReadU32(unsafe.Pointer(&d[sp])))
					sp += 4
					poolSectionIds[fi] = safeReadU32(unsafe.Pointer(&d[sp]))
					sp += 4
				}
				s.groupFieldWidths[gi] = widths
				s.groupFieldOffsets[gi] = offsets
				s.groupFieldNative[gi] = natives
				s.groupFieldNativeType[gi] = natTypes
				s.groupFieldIds[gi] = fieldIds
				s.groupPoolSectionIds[gi] = poolSectionIds
			} else {
				sp += uint64(fldCount * 12)
			}
		}
	}

	for g := 0; g < actualGroups; g++ {
		if s.groupStrides[g] == 0 {
			s.groupStrides[g] = s.groupFieldCounts[g] * s.poolIdxSize
		}
		if s.groupFieldWidths[g] == nil {
			s.groupFieldWidths[g] = make([]int, s.groupFieldCounts[g])
			for i := range s.groupFieldWidths[g] {
				s.groupFieldWidths[g][i] = s.poolIdxSize
			}
		}
		if s.groupFieldOffsets[g] == nil {
			s.groupFieldOffsets[g] = make([]int, s.groupFieldCounts[g])
			for i := range s.groupFieldOffsets[g] {
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

	s.resolveFieldNames()
	s.poolsLoaded = false
	s.groupPools = nil
	return nil
}

func (s *QzdbSearcher) resolveFieldNames() {
	d := s.data
	offMeta := s.offMeta
	if s.flags&4 != 0 && offMeta > 0 && offMeta+4 <= uint64(len(d)) {
		var fieldNames []string
		pos := offMeta
		size := uint64(len(d))
		for pos+4 <= size {
			t := d[pos]
			length := uint64(safeReadU16(unsafe.Pointer(&d[pos+2])))
			if t == 0 || length == 0 {
				break
			}
			val := string(d[pos+4 : pos+4+length])
			if t == 1 {
				s.versionName = val
			} else if t == 2 {
				fieldNames = strings.Split(val, "|")
			}
			pos += 4 + length
		}

		if fieldNames != nil && len(fieldNames) == s.groupFieldCounts[0] {
			s.fieldNames = fieldNames
			s.floatFieldIndices = make(map[string]bool)
			s.fieldNameToIdx = make(map[string]int, len(fieldNames))
			for i, n := range fieldNames {
				s.fieldNameToIdx[n] = i
				if floatFields[n] {
					s.floatFieldIndices[n] = true
				}
			}
			return
		}
	}

	s.fieldNames = make([]string, s.groupFieldCounts[0])
	for i := range s.fieldNames {
		s.fieldNames[i] = fmt.Sprintf("field_%d", i)
	}
	s.floatFieldIndices = make(map[string]bool)
	// Build reverse name→index cache for FindFields projection
	s.fieldNameToIdx = make(map[string]int, len(s.fieldNames))
	for i, n := range s.fieldNames {
		s.fieldNameToIdx[n] = i
	}
}

func (s *QzdbSearcher) ensurePoolsLoaded() {
	if s.poolsLoaded {
		return
	}
	s.poolsLoaded = true

	groupCount := len(s.groupFieldCounts)
	s.groupPools = make([][][]string, groupCount)

	if s.offPools <= 0 {
		return
	}

	poolCursor := s.offPools
	poolEnd := s.offMeta
	if poolEnd <= 0 {
		poolEnd = uint64(len(s.data))
	}
	d := s.data

	for g := 0; g < groupCount; g++ {
		fieldCount := s.groupFieldCounts[g]
		groupPoolList := make([][]string, fieldCount)
		natives := s.groupFieldNative[g]
		for f := 0; f < fieldCount; f++ {
			if natives != nil && f < len(natives) && natives[f] {
				groupPoolList[f] = []string{}
				continue
			}

			if poolCursor+4 > poolEnd {
				groupPoolList[f] = []string{}
				continue
			}

			count := safeReadU32(unsafe.Pointer(&d[poolCursor]))
			poolCursor += 4
			if s.offRowSchema > 0 {
				poolCursor += 4
			}
			const maxPoolCount = 1 << 26
			if count == 0 || count > maxPoolCount {
				groupPoolList[f] = []string{}
				continue
			}
			if poolCursor+uint64(count+1)*4 > poolEnd {
				groupPoolList[f] = []string{}
				continue
			}

			offsets := make([]uint32, count+1)
			for o := range offsets {
				offsets[o] = safeReadU32(unsafe.Pointer(&d[poolCursor]))
				poolCursor += 4
			}

			stringsList := make([]string, count)
			for idx := uint32(0); idx < count; idx++ {
				start := offsets[idx]
				end := offsets[idx+1]
				length := end - start
				if length > 0 {
					segStart := poolCursor + uint64(start)
					segEnd := poolCursor + uint64(end)
					if segEnd <= uint64(len(d)) && segStart <= segEnd {
						stringsList[idx] = string(d[segStart:segEnd])
					}
				}
			}
			poolCursor += uint64(offsets[count])
			groupPoolList[f] = stringsList
		}
		s.groupPools[g] = groupPoolList
	}
}

func (s *QzdbSearcher) getV4Child(nodeIdx uint32, bit uint32) uint32 {
	if nodeIdx >= s.v4NodeCount {
		return 0
	}
	d := s.data
	if s.v4Node24 {
		nodeOffset := s.offV4Nodes + uint64(nodeIdx)*6
		offset := nodeOffset
		if bit != 0 {
			offset = nodeOffset + 3
		}
		if offset+3 >= uint64(len(d)) {
			return 0
		}
		val := uint32(d[offset]) | uint32(d[offset+1])<<8 | uint32(d[offset+2])<<16
		if val&0x800000 != 0 {
			return (val & SENTINEL_MASK_24) | SENTINEL
		}
		return val
	} else {
		childOff := s.offV4Nodes + uint64(nodeIdx)*8 + uint64(bit)*4
		if childOff+4 > uint64(len(d)) {
			return 0
		}
		return safeReadU32(unsafe.Pointer(&d[childOff]))
	}
}

func (s *QzdbSearcher) getV6Child(nodeIdx uint32, bit uint32) uint32 {
	if nodeIdx >= s.v6NodeCount {
		return 0
	}
	d := s.data
	if s.v6Node24 {
		nodeOffset := s.offV6Nodes + uint64(nodeIdx)*6
		offset := nodeOffset
		if bit != 0 {
			offset = nodeOffset + 3
		}
		if offset+3 >= uint64(len(d)) {
			return 0
		}
		val := uint32(d[offset]) | uint32(d[offset+1])<<8 | uint32(d[offset+2])<<16
		if val&0x800000 != 0 {
			return (val & SENTINEL_MASK_24) | SENTINEL
		}
		return val
	} else {
		childOff := s.offV6Nodes + uint64(nodeIdx)*8 + uint64(bit)*4
		if childOff+4 > uint64(len(d)) {
			return 0
		}
		return safeReadU32(unsafe.Pointer(&d[childOff]))
	}
}

func (s *QzdbSearcher) trieWalkV4(ipInt uint32) (uint32, error) {
	hi16 := (ipInt >> 16) & 0xFFFF
	ptr := safeReadU32(unsafe.Pointer(&s.data[s.offV4Jump+uint64(hi16)*4]))

	if ptr == 0 {
		return 0, nil
	}
	if ptr&SENTINEL != 0 {
		return ptr & SENTINEL_MASK_31, nil
	}

	idx := ptr
	suffix := (ipInt & 0xFFFF) << 16

	steps := 0
	for {
		bit := (suffix >> 31) & 1
		child := s.getV4Child(idx, bit)

		if child == 0 {
			return 0, nil
		}
		if child&SENTINEL != 0 {
			return child & SENTINEL_MASK_31, nil
		}

		idx = child
		suffix <<= 1
		steps++
		if steps >= maxTrieWalkSteps {
			return 0, ErrCorrupted
		}
	}
}

func (s *QzdbSearcher) trieWalkV6(ip16 [16]byte) (uint32, error) {
	hi := binary.BigEndian.Uint64(ip16[:8])
	lo := binary.BigEndian.Uint64(ip16[8:16])

	// Jump index = top v6JumpBits bits of the 128-bit address, held in the top bits of hi.
	idxJump := (hi >> (64 - uint(s.v6JumpBits))) & uint64((1<<s.v6JumpBits)-1)

	ptr := safeReadU32(unsafe.Pointer(&s.data[s.offV6Jump+idxJump*4]))
	if ptr == 0 {
		return 0, nil
	}
	if ptr&SENTINEL != 0 {
		return ptr & SENTINEL_MASK_31, nil
	}

	idx := ptr
	depth := s.v6JumpBits
	steps := 0

	for depth < 128 {
		var bit uint32
		if depth < 64 {
			bit = uint32((hi >> (63 - depth)) & 1)
		} else {
			bit = uint32((lo >> (127 - depth)) & 1)
		}
		child := s.getV6Child(idx, bit)

		if child == 0 {
			return 0, nil
		}
		if child&SENTINEL != 0 {
			return child & SENTINEL_MASK_31, nil
		}

		idx = child
		depth++
		steps++
		if steps >= maxTrieWalkSteps {
			return 0, ErrCorrupted
		}
	}

	return 0, nil
}

func (s *QzdbSearcher) readIPRow(rowID uint32) (uint32, uint32, uint32) {
	if rowID <= 0 || rowID >= uint32(s.rowCount) {
		return 0, 0, 0
	}
	off := s.offIPRow + uint64(rowID)*uint64(s.ipRowSize)
	geoID := s.safeReadU24(off)
	asnID := s.safeReadU24(off + 3)

	var usageTypeID uint32
	if s.ipRowSize >= 9 {
		usageTypeID = s.safeReadU24(off + 6)
	}

	return geoID, asnID, usageTypeID
}

func (s *QzdbSearcher) resolveRowID(rowID uint32, groupIndex int) (*GeoInfo, error) {
	if s == nil {
		return nil, ErrInvalidParam
	}
	geoID, asnID, usageTypeID := s.readIPRow(rowID)
	var mask uint16
	if groupIndex < len(s.groupDimMasks) {
		mask = s.groupDimMasks[groupIndex]
	}

	var entryID uint32
	if mask&0x02 != 0 {
		entryID = asnID
	} else if mask&0x04 != 0 {
		entryID = usageTypeID
	} else {
		entryID = geoID
	}

	if entryID == 0 {
		return nil, ErrNotFound
	}
	return s.resolveGeo(entryID, groupIndex)
}

func (s *QzdbSearcher) resolveGeo(entryID uint32, groupIndex int) (*GeoInfo, error) {
	if s == nil {
		return nil, ErrInvalidParam
	}
	if groupIndex < 0 || groupIndex >= len(s.groupFieldCounts) {
		return nil, ErrInvalidParam
	}
	_ = entryID
	if entryID >= s.groupEntryCounts[groupIndex] {
		return nil, ErrOutOfBounds
	}

	s.ensurePoolsLoaded()

	fieldCount := s.groupFieldCounts[groupIndex]
	if fieldCount <= 0 {
		return nil, ErrCorrupted
	}

	groupEntryStart := s.offGeoEntries + s.groupEntryOffsets[groupIndex]
	stride := uint64(s.groupStrides[groupIndex])
	entryOffset := groupEntryStart + uint64(entryID)*stride
	d := s.data

	widths := s.groupFieldWidths[groupIndex]
	baseOffsets := s.groupFieldOffsets[groupIndex]
	natives := s.groupFieldNative[groupIndex]
	natTypes := s.groupFieldNativeType[groupIndex]

	values := make([]string, fieldCount)
	for i := 0; i < fieldCount; i++ {
		w := widths[i]
		fo := entryOffset + uint64(baseOffsets[i])
		isNative := natives != nil && i < len(natives) && natives[i]

		if isNative {
			t := 0
			if natTypes != nil && i < len(natTypes) {
				t = natTypes[i]
			}
			if t == 1 {
				if w == 4 {
					bits := safeReadU32(unsafe.Pointer(&d[fo]))
					values[i] = strconv.FormatFloat(float64(math.Float32frombits(bits)), 'f', -1, 32)
				} else {
					bits := safeReadU64(unsafe.Pointer(&d[fo]))
					values[i] = strconv.FormatFloat(math.Float64frombits(bits), 'f', -1, 64)
				}
			} else {
				valNum := s.safeReadUintWidth(fo, w)
				values[i] = strconv.FormatUint(uint64(valNum), 10)
			}
		} else {
			idx := s.safeReadUintWidth(fo, w)
			groupPool := s.groupPools[groupIndex]
			if groupPool != nil && i < len(groupPool) && int(idx) < len(groupPool[i]) {
				values[i] = groupPool[i][idx]
			}
		}
	}

	return &GeoInfo{FieldNames: s.fieldNames, floatIndices: s.floatFieldIndices, Values: values}, nil
}

func (s *QzdbSearcher) Find(ipStr string) (*GeoInfo, error) {
	if s == nil {
		return nil, ErrInvalidParam
	}
	if ipStr == "" {
		return nil, ErrInvalidParam
	}

	result, ok := fastParseIp(ipStr)
	if !ok {
		return nil, ErrInvalidParam
	}
	if result.isV4 {
		return s.FindUint(result.v4)
	}
	return s.FindV6Uint(result.v6)
}

func (s *QzdbSearcher) FindUint(ipInt uint32) (*GeoInfo, error) {
	if s == nil {
		return nil, ErrInvalidParam
	}
	if !s.hasV4 {
		return nil, ErrNotFound
	}
	rowID, err := s.trieWalkV4(ipInt)
	if err != nil {
		return nil, err
	}
	if rowID == 0 {
		return nil, ErrNotFound
	}
	return s.resolveRowID(rowID, s.groupIndex)
}

func (s *QzdbSearcher) FindV6Uint(ip16 [16]byte) (*GeoInfo, error) {
	if s == nil {
		return nil, ErrInvalidParam
	}
	if !s.hasV6 {
		return nil, ErrNotFound
	}
	rowID, err := s.trieWalkV6(ip16)
	if err != nil {
		return nil, err
	}
	if rowID == 0 {
		return nil, ErrNotFound
	}
	return s.resolveRowID(rowID, s.groupIndex)
}

// LookupRowId returns the raw row_id for an IP string (trie walk only, no data materialization).
// Returns 0 if not found.
func (s *QzdbSearcher) LookupRowId(ipStr string) uint32 {
	if ipStr == "" {
		return 0
	}
	result, ok := fastParseIp(ipStr)
	if !ok {
		return 0
	}
	if result.isV4 {
		return s.LookupRowIdUint(result.v4)
	}
	if !s.hasV6 {
		return 0
	}
	rowID, _ := s.trieWalkV6(result.v6)
	return rowID
}

// LookupRowIdUint returns the raw row_id for a pre-parsed IPv4 integer.
func (s *QzdbSearcher) LookupRowIdUint(ipInt uint32) uint32 {
	if !s.hasV4 {
		return 0
	}
	rowID, _ := s.trieWalkV4(ipInt)
	return rowID
}

// LookupRowIdV6 returns the raw row_id for a 128-bit IPv6 integer.
func (s *QzdbSearcher) LookupRowIdV6(ip16 [16]byte) uint32 {
	if !s.hasV6 {
		return 0
	}
	rowID, _ := s.trieWalkV6(ip16)
	return rowID
}

// LookupIds returns the raw entry IDs (geoId, asnId, usageId) for a row_id.
// Returns false if row_id is invalid.
func (s *QzdbSearcher) LookupIds(rowId uint32) (geoId, asnId, usageId uint32, ok bool) {
	if rowId == 0 || rowId >= uint32(s.rowCount) {
		return 0, 0, 0, false
	}
	geoId, asnId, usageId = s.readIPRow(rowId)
	return geoId, asnId, usageId, true
}

func (s *QzdbSearcher) FindStr(ipStr string) (string, error) {
	if s == nil {
		return "", ErrInvalidParam
	}
	info, err := s.Find(ipStr)
	if err != nil {
		return "", err
	}
	if info == nil {
		return "", ErrNotFound
	}
	return info.ToPipe(), nil
}

// FindFields resolves only the specified fields for an IP.
// fieldNames=nil or empty returns full GeoInfo (backward compatible).
func (s *QzdbSearcher) FindFields(ipStr string, fieldNames []string) (*GeoInfo, error) {
	if s == nil {
		return nil, ErrInvalidParam
	}
	if len(fieldNames) == 0 {
		return s.Find(ipStr)
	}
	rowID := s.LookupRowId(ipStr)
	if rowID == 0 {
		return nil, ErrNotFound
	}
	return s.resolveGeoFields(rowID, s.groupIndex, fieldNames)
}

func (s *QzdbSearcher) resolveGeoFields(rowID uint32, groupIndex int, fieldNames []string) (*GeoInfo, error) {
	if s == nil {
		return nil, ErrInvalidParam
	}
	geoID, asnID, usageTypeID := s.readIPRow(rowID)
	var mask uint16
	if groupIndex < len(s.groupDimMasks) {
		mask = s.groupDimMasks[groupIndex]
	}
	var entryID uint32
	if mask&0x02 != 0 {
		entryID = asnID
	} else if mask&0x04 != 0 {
		entryID = usageTypeID
	} else {
		entryID = geoID
	}
	if entryID == 0 || groupIndex < 0 || groupIndex >= len(s.groupFieldCounts) {
		return nil, ErrNotFound
	}
	if entryID >= s.groupEntryCounts[groupIndex] {
		return nil, ErrOutOfBounds
	}
	s.ensurePoolsLoaded()
	fieldCount := s.groupFieldCounts[groupIndex]
	if fieldCount <= 0 {
		return nil, ErrCorrupted
	}
	// Collect requested field indices using cached map
	indices := make([]int, 0, len(fieldNames))
	for _, name := range fieldNames {
		if idx, ok := s.fieldNameToIdx[name]; ok {
			indices = append(indices, idx)
		}
	}
	if len(indices) == 0 {
		return nil, ErrInvalidParam
	}
	groupEntryStart := s.offGeoEntries + s.groupEntryOffsets[groupIndex]
	stride := uint64(s.groupStrides[groupIndex])
	entryOffset := groupEntryStart + uint64(entryID)*stride
	d := s.data
	widths := s.groupFieldWidths[groupIndex]
	baseOffsets := s.groupFieldOffsets[groupIndex]
	natives := s.groupFieldNative[groupIndex]
	natTypes := s.groupFieldNativeType[groupIndex]

	values := make([]string, fieldCount)
	for _, i := range indices {
		if i < 0 || i >= fieldCount {
			continue
		}
		w := widths[i]
		fo := entryOffset + uint64(baseOffsets[i])
		isNative := natives != nil && i < len(natives) && natives[i]
		if isNative {
			t := 0
			if natTypes != nil && i < len(natTypes) {
				t = natTypes[i]
			}
			if t == 1 {
				if w == 4 {
					bits := safeReadU32(unsafe.Pointer(&d[fo]))
					values[i] = strconv.FormatFloat(float64(math.Float32frombits(bits)), 'f', -1, 32)
				} else {
					bits := safeReadU64(unsafe.Pointer(&d[fo]))
					values[i] = strconv.FormatFloat(math.Float64frombits(bits), 'f', -1, 64)
				}
			} else {
				valNum := s.safeReadUintWidth(fo, w)
				values[i] = strconv.FormatUint(uint64(valNum), 10)
			}
		} else {
			idx := s.safeReadUintWidth(fo, w)
			groupPool := s.groupPools[groupIndex]
			if groupPool != nil && i < len(groupPool) && int(idx) < len(groupPool[i]) {
				values[i] = groupPool[i][idx]
			}
		}
	}
	return &GeoInfo{FieldNames: s.fieldNames, floatIndices: s.floatFieldIndices, Values: values}, nil
}

// Reload atomically replaces the database state with a fresh load from path.
func (s *QzdbSearcher) Reload(path string) error {
	ns, err := NewSearcher(path, s.groupIndex)
	if err != nil {
		return err
	}
	*s = *ns // atomic struct copy (all fields replaced at once)
	return nil
}

func (s *QzdbSearcher) FieldNames() []string {
	return s.fieldNames
}

func (s *QzdbSearcher) Version() string {
	return s.versionName
}

func (s *QzdbSearcher) PoolCount() int {
	return s.poolCount
}

func (s *QzdbSearcher) VerifyCRC() bool {
	if len(s.data) < 20 {
		return false
	}
	stored := binary.LittleEndian.Uint32(s.data[16:20])

	// Segmented CRC: process bytes 0-15, then 4 zero bytes, then bytes 20+
	crc := crc32.Update(0, crc32.IEEETable, s.data[:16])
	crc = crc32.Update(crc, crc32.IEEETable, []byte{0, 0, 0, 0})
	crc = crc32.Update(crc, crc32.IEEETable, s.data[20:])
	return stored == crc
}

var hexLUT [128]byte

func init() {
	for i := 0; i < 10; i++ {
		hexLUT[48+i] = byte(i)
	}
	for i := 0; i < 6; i++ {
		hexLUT[97+i] = byte(10 + i)
		hexLUT[65+i] = byte(10 + i)
	}
}

func fastParseIpv4(s string) (uint32, bool) {
	n := len(s)
	if n == 0 || s[n-1] == '.' {
		return 0, false
	}
	var result, val uint32
	dots, start := 0, 0
	for i := 0; i <= n; i++ {
		var c byte = '.'
		if i < n {
			c = s[i]
		}
		if c == '.' {
			segLen := i - start
			if segLen == 0 || segLen > 3 {
				return 0, false
			}
			if segLen > 1 && s[start] == '0' {
				return 0, false
			}
			val = 0
			for j := start; j < i; j++ {
				d := s[j]
				if d < '0' || d > '9' {
					return 0, false
				}
				val = val*10 + uint32(d-'0')
			}
			if val > 255 {
				return 0, false
			}
			result = (result << 8) | val
			dots++
			start = i + 1
		}
	}
	if dots != 4 {
		return 0, false
	}
	return result, true
}

type parseResult struct {
	v4 uint32
	v6 [16]byte
	isV4 bool
}

func fastParseIp(s string) (*parseResult, bool) {
	n := len(s)
	// Reject whitespace — SSRF-safe, cross-language consistent
	for i := 0; i < n; i++ {
		c := s[i]
		if c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f' {
			return nil, false
		}
	}
	if n == 0 || n > 45 {
		return nil, false
	}
	if !strings.Contains(s, ":") {
		v4, ok := fastParseIpv4(s)
		if !ok {
			return nil, false
		}
		return &parseResult{v4: v4, isV4: true}, true
	}
	if strings.Contains(s, "%") {
		return nil, false
	}
	dc := strings.Index(s, "::")
	if dc >= 0 && strings.Index(s[dc+2:], "::") >= 0 {
		return nil, false
	}
	var lft, rgt string
	if dc >= 0 {
		lft = s[:dc]
		rgt = s[dc+2:]
	} else {
		lft = s
	}
	lg := strings.Split(lft, ":")
	rg := strings.Split(rgt, ":")
	if lft == "" {
		lg = nil
	}
	if rgt == "" {
		rg = nil
	}
	for _, g := range lg {
		if g == "" {
			return nil, false
		}
	}
	for _, g := range rg {
		if g == "" {
			return nil, false
		}
	}
	allg := make([]string, 0, len(lg)+len(rg))
	allg = append(allg, lg...)
	allg = append(allg, rg...)
	hasV4 := false
	var v4Int uint32
	if len(allg) > 0 && strings.Contains(allg[len(allg)-1], ".") {
		v, ok := fastParseIpv4(allg[len(allg)-1])
		if !ok {
			return nil, false
		}
		v4Int = v
		hasV4 = true
		allg = allg[:len(allg)-1]
	}
	ng := len(allg)
	v4Slots := 0
	if hasV4 {
		v4Slots = 2
	}
	if dc >= 0 {
		if ng+v4Slots > 7 {
			return nil, false
		}
	} else {
		if ng+v4Slots != 8 {
			return nil, false
		}
	}
	for _, g := range allg {
		gl := len(g)
		if gl == 0 || gl > 4 {
			return nil, false
		}
		for j := 0; j < gl; j++ {
			cc := g[j]
			if cc >= 128 || (hexLUT[cc] == 0 && cc != '0') {
				return nil, false
			}
		}
	}
	zeros := 8 - ng - v4Slots
	var buf [16]byte
	off := 0
	for _, g := range lg {
		v := uint16(0)
		for j := 0; j < len(g); j++ {
			v = (v << 4) | uint16(hexLUT[g[j]])
		}
		buf[off] = byte(v >> 8)
		buf[off+1] = byte(v)
		off += 2
	}
	off += zeros * 2
	for _, g := range rg {
		v := uint16(0)
		for j := 0; j < len(g); j++ {
			v = (v << 4) | uint16(hexLUT[g[j]])
		}
		buf[off] = byte(v >> 8)
		buf[off+1] = byte(v)
		off += 2
	}
	if hasV4 {
		buf[12] = byte(v4Int >> 24)
		buf[13] = byte(v4Int >> 16)
		buf[14] = byte(v4Int >> 8)
		buf[15] = byte(v4Int)
	}
	if buf[10] == 0xff && buf[11] == 0xff &&
		buf[0] == 0 && buf[1] == 0 && buf[2] == 0 && buf[3] == 0 &&
		buf[4] == 0 && buf[5] == 0 && buf[6] == 0 && buf[7] == 0 &&
		buf[8] == 0 && buf[9] == 0 {
		return &parseResult{v4: uint32(buf[12])<<24 | uint32(buf[13])<<16 | uint32(buf[14])<<8 | uint32(buf[15]), isV4: true}, true
	}
	return &parseResult{v6: buf}, true
}
