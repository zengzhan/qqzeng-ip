package qzdb

import (
	"encoding/binary"
	"fmt"
	"hash/crc32"
	"math"
	"math/big"
	"net"
	"os"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"unsafe"
)

const SENTINEL uint32 = 0x80000000

var floatFields = map[string]bool{
	"longitude": true,
	"latitude":  true,
}

type GeoInfo struct {
	fields       map[string]string
	FieldNames   []string
	floatIndices map[string]bool
	Values       []string
}

func (g *GeoInfo) Get(name string) string {
	if val, ok := g.fields[name]; ok {
		return val
	}
	return ""
}

func (g *GeoInfo) ToPipe() string {
	parts := make([]string, len(g.FieldNames))
	for i, fname := range g.FieldNames {
		val := g.fields[fname]
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

type QzdbSearcher struct {
	data                []byte
	groupIndex          int
	fieldNames          []string
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
	return instance, initErr
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
func readU16(p unsafe.Pointer) uint16 {
	b := (*[2]byte)(p)
	return uint16(b[0]) | uint16(b[1])<<8
}

//go:nosplit
func readU32(p unsafe.Pointer) uint32 {
	b := (*[4]byte)(p)
	return uint32(b[0]) | uint32(b[1])<<8 | uint32(b[2])<<16 | uint32(b[3])<<24
}

//go:nosplit
func readU64(p unsafe.Pointer) uint64 {
	b := (*[8]byte)(p)
	return uint64(b[0]) | uint64(b[1])<<8 | uint64(b[2])<<16 | uint64(b[3])<<24 |
		uint64(b[4])<<32 | uint64(b[5])<<40 | uint64(b[6])<<48 | uint64(b[7])<<56
}

func (s *QzdbSearcher) readU24(off uint64) uint32 {
	d := s.data
	return uint32(d[off]) | uint32(d[off+1])<<8 | uint32(d[off+2])<<16
}

func (s *QzdbSearcher) readU48(off uint64) uint64 {
	d := s.data
	return uint64(d[off]) | uint64(d[off+1])<<8 | uint64(d[off+2])<<16 |
		uint64(d[off+3])<<24 | uint64(d[off+4])<<32 | uint64(d[off+5])<<40
}

func (s *QzdbSearcher) readUintWidth(off uint64, width int) uint32 {
	if width <= 1 {
		return uint32(s.data[off])
	} else if width == 2 {
		return uint32(readU16(unsafe.Pointer(&s.data[off])))
	} else if width == 3 {
		return s.readU24(off)
	} else {
		return readU32(unsafe.Pointer(&s.data[off]))
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

	s.flags = readU16(unsafe.Pointer(&d[8]))
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
	s.geoCount = int(readU16(unsafe.Pointer(&d[14])))
	s.rowCount = int(readU32(unsafe.Pointer(&d[20])))
	s.v4RecCount = readU32(unsafe.Pointer(&d[24]))
	s.v6RecCount = readU32(unsafe.Pointer(&d[28]))

	hs := readU32(unsafe.Pointer(&d[36]))
	if hs != 192 {
		return fmt.Errorf("unexpected header size: %d", hs)
	}

	s.offRowSchema = readU64(unsafe.Pointer(&d[40]))
	s.offGroupSchema = readU64(unsafe.Pointer(&d[48]))
	s.offV4Jump = readU64(unsafe.Pointer(&d[64]))
	s.offV4Nodes = readU64(unsafe.Pointer(&d[72]))
	s.offV6Jump = readU64(unsafe.Pointer(&d[80]))
	s.offV6Nodes = readU64(unsafe.Pointer(&d[88]))
	s.offIPRow = readU64(unsafe.Pointer(&d[96]))
	s.offGeoEntries = readU64(unsafe.Pointer(&d[104]))
	s.offPools = readU64(unsafe.Pointer(&d[136]))
	s.offMeta = readU64(unsafe.Pointer(&d[144]))

	s.v4NodeCount = readU32(unsafe.Pointer(&d[152]))
	s.v6NodeCount = readU32(unsafe.Pointer(&d[156]))
	s.ipRowSize = int(readU32(unsafe.Pointer(&d[160])))
	if s.ipRowSize < 1 || s.ipRowSize > 64 {
		return fmt.Errorf("ipRowSize out of range [1,64]: %d", s.ipRowSize)
	}
	s.geoEntryGroupCount = int(readU32(unsafe.Pointer(&d[164])))
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
		s.groupEntryOffsets[i] = s.readU48(off)
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
			s.groupEntryCounts[gi] = readU32(unsafe.Pointer(&d[gmOff]))
			gmOff += 4
		} else {
			s.groupEntryCounts[gi] = uint32(readU16(unsafe.Pointer(&d[gmOff])))
			gmOff += 2
		}

		if fmtVer == 1 || fmtVer >= 3 {
			s.groupDimMasks[gi] = readU16(unsafe.Pointer(&d[gmOff]))
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
		gsGroupCount := int(readU16(unsafe.Pointer(&d[sp])))
		sp += 2
		maxGsGroups := gsGroupCount
		if actualGroups < maxGsGroups {
			maxGsGroups = actualGroups
		}
		for gi := 0; gi < maxGsGroups; gi++ {
			sp += 2 // skip groupId
			fldCount := int(readU16(unsafe.Pointer(&d[sp])))
			sp += 2
			sp += 4 // skip entryCount
			stride := int(readU32(unsafe.Pointer(&d[sp])))
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
					fieldIds[fi] = readU16(unsafe.Pointer(&d[sp]))
					sp += 2
					widths[fi] = int(d[sp])
					sp++
					fieldFlags := d[sp]
					sp++
					natives[fi] = (fieldFlags & 0x01) != 0
					natTypes[fi] = int((fieldFlags >> 1) & 0x03)
					offsets[fi] = int(readU32(unsafe.Pointer(&d[sp])))
					sp += 4
					poolSectionIds[fi] = readU32(unsafe.Pointer(&d[sp]))
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
			length := uint64(readU16(unsafe.Pointer(&d[pos+2])))
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
			for _, n := range fieldNames {
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
				continue;
			}
		count := readU32(unsafe.Pointer(&d[poolCursor]))
		poolCursor += 4
		if s.offRowSchema > 0 {
			poolCursor += 4
		}
		// Cap count to a sane maximum to avoid OOM / OOB on corrupt files.
		const maxPoolCount = 1 << 26
		if count == 0 || count > maxPoolCount {
			groupPoolList[f] = []string{}
			continue
		}
		// Ensure the offset table (count+1 uint32s) stays within the pool region.
		if poolCursor+uint64(count+1)*4 > poolEnd {
			groupPoolList[f] = []string{}
			continue
		}

		offsets := make([]uint32, count+1)
		for o := range offsets {
			offsets[o] = readU32(unsafe.Pointer(&d[poolCursor]))
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
			return (val & 0x7FFFFF) | SENTINEL
		}
		return val
	} else {
		childOff := s.offV4Nodes + uint64(nodeIdx)*8 + uint64(bit)*4
		if childOff+4 > uint64(len(d)) {
			return 0
		}
		return readU32(unsafe.Pointer(&d[childOff]))
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
			return (val & 0x7FFFFF) | SENTINEL
		}
		return val
	} else {
		childOff := s.offV6Nodes + uint64(nodeIdx)*8 + uint64(bit)*4
		if childOff+4 > uint64(len(d)) {
			return 0
		}
		return readU32(unsafe.Pointer(&d[childOff]))
	}
}

func (s *QzdbSearcher) trieWalkV4(ipInt uint32) uint32 {
	hi16 := (ipInt >> 16) & 0xFFFF
	ptr := readU32(unsafe.Pointer(&s.data[s.offV4Jump+uint64(hi16)*4]))

	if ptr == 0 {
		return 0
	}
	if ptr&SENTINEL != 0 {
		return ptr & 0x7FFFFFFF
	}

	idx := ptr
	suffix := (ipInt & 0xFFFF) << 16

	steps := 0
	for {
		bit := (suffix >> 31) & 1
		child := s.getV4Child(idx, bit)

		if child == 0 {
			return 0
		}
		if child&SENTINEL != 0 {
			return child & 0x7FFFFFFF
		}

		idx = child
		suffix <<= 1
		steps++
		if steps > 32 {
			return 0
		}
	}
}

func (s *QzdbSearcher) trieWalkV6(ipInt *big.Int) uint32 {
	// Convert big.Int to two uint64 (lo = most-significant 64 bits, hi = least-significant 64 bits)
	// using the big-endian byte convention established by SetBytes. No per-bit big.Int allocation.
	var hi, lo uint64
	if ipInt.BitLen() <= 64 {
		lo = ipInt.Uint64()
	} else {
		// Rsh by 64 bits to get the upper (most-significant) half, then extract hi/lo as uint64.
		// Avoids ipInt.Bytes() heap allocation and padding/truncation logic.
		var tmp big.Int
		lo = tmp.Rsh(ipInt, 64).Uint64()
		hi = ipInt.Uint64()
	}

	// Jump index = top v6JumpBits bits of the 128-bit address, held in the top bits of lo.
	idxJump := (lo >> (64 - uint(s.v6JumpBits))) & uint64((1<<s.v6JumpBits)-1)

	ptr := readU32(unsafe.Pointer(&s.data[s.offV6Jump+idxJump*4]))
	if ptr == 0 {
		return 0
	}
	if ptr&SENTINEL != 0 {
		return ptr & 0x7FFFFFFF
	}

	idx := ptr
	depth := s.v6JumpBits

	for depth < 128 {
		var bit uint32
		if depth < 64 {
			bit = uint32((lo >> (63 - depth)) & 1)
		} else {
			bit = uint32((hi >> (127 - depth)) & 1)
		}
		child := s.getV6Child(idx, bit)

		if child == 0 {
			return 0
		}
		if child&SENTINEL != 0 {
			return child & 0x7FFFFFFF
		}

		idx = child
		depth++
	}

	return 0
}

func (s *QzdbSearcher) readIPRow(rowID uint32) (uint32, uint32, uint32) {
	if rowID <= 0 || rowID >= uint32(s.rowCount) {
		return 0, 0, 0
	}
	off := s.offIPRow + uint64(rowID)*uint64(s.ipRowSize)
	geoID := s.readU24(off)
	asnID := s.readU24(off + 3)

	var usageTypeID uint32
	if s.ipRowSize >= 9 {
		usageTypeID = s.readU24(off + 6)
	}

	return geoID, asnID, usageTypeID
}

func (s *QzdbSearcher) resolveRowID(rowID uint32, groupIndex int) *GeoInfo {
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
		return nil
	}
	return s.resolveGeo(entryID, groupIndex)
}

func (s *QzdbSearcher) resolveGeo(entryID uint32, groupIndex int) *GeoInfo {
	if groupIndex < 0 || groupIndex >= len(s.groupFieldCounts) {
		return nil
	}
	if entryID < 0 {
		return nil
	}
	if entryID >= s.groupEntryCounts[groupIndex] {
		return nil
	}

	s.ensurePoolsLoaded()

	fieldCount := s.groupFieldCounts[groupIndex]
	if fieldCount <= 0 {
		return nil
	}

	groupEntryStart := s.offGeoEntries + s.groupEntryOffsets[groupIndex]
	stride := uint64(s.groupStrides[groupIndex])
	entryOffset := groupEntryStart + uint64(entryID)*stride
	d := s.data

	widths := s.groupFieldWidths[groupIndex]
	baseOffsets := s.groupFieldOffsets[groupIndex]
	natives := s.groupFieldNative[groupIndex]
	natTypes := s.groupFieldNativeType[groupIndex]

	fields := make(map[string]string)
	for i := 0; i < fieldCount; i++ {
		w := widths[i]
		fo := entryOffset + uint64(baseOffsets[i])
		isNative := natives != nil && i < len(natives) && natives[i]

		var val string
		if isNative {
			t := 0
			if natTypes != nil && i < len(natTypes) {
				t = natTypes[i]
			}
			if t == 1 {
				if w == 4 {
					bits := readU32(unsafe.Pointer(&d[fo]))
					val = strconv.FormatFloat(float64(math.Float32frombits(bits)), 'f', -1, 32)
				} else {
					bits := readU64(unsafe.Pointer(&d[fo]))
					val = strconv.FormatFloat(math.Float64frombits(bits), 'f', -1, 64)
				}
			} else {
				valNum := s.readUintWidth(fo, w)
				val = strconv.FormatUint(uint64(valNum), 10)
			}
		} else {
			idx := s.readUintWidth(fo, w)
			groupPool := s.groupPools[groupIndex]
			if groupPool != nil && i < len(groupPool) && int(idx) < len(groupPool[i]) {
				val = groupPool[i][idx]
			}
		}

		var fname string
		if i < len(s.fieldNames) {
			fname = s.fieldNames[i]
		} else {
			fname = fmt.Sprintf("field_%d", i)
		}
		fields[fname] = val
	}

	values := make([]string, fieldCount)
	for i := 0; i < fieldCount; i++ {
		var fname string
		if i < len(s.fieldNames) {
			fname = s.fieldNames[i]
		} else {
			fname = fmt.Sprintf("field_%d", i)
		}
		values[i] = fields[fname]
	}

	return &GeoInfo{fields: fields, FieldNames: s.fieldNames, floatIndices: s.floatFieldIndices, Values: values}
}

func (s *QzdbSearcher) Find(ipStr string) *GeoInfo {
	if ipStr == "" {
		return nil
	}

	if strings.Contains(ipStr, ":") {
		ip := net.ParseIP(ipStr)
		if ip == nil {
			return nil
		}
		ip16 := ip.To16()
		if ip16 == nil {
			return nil
		}
		// Check for IPv4-mapped IPv6 (::ffff:x.x.x.x)
		if ip16[0] == 0 && ip16[1] == 0 && ip16[2] == 0 && ip16[3] == 0 &&
			ip16[4] == 0 && ip16[5] == 0 && ip16[6] == 0 && ip16[7] == 0 &&
			ip16[8] == 0 && ip16[9] == 0 && ip16[10] == 0xff && ip16[11] == 0xff {
			return s.FindUint(binary.BigEndian.Uint32(ip16[12:16]))
		}

		ipInt := new(big.Int).SetBytes(ip16)
		return s.FindV6Uint(ipInt)
	}

	ipInt, ok := fastParseIpV4(ipStr)
	if !ok {
		return nil
	}
	return s.FindUint(ipInt)
}

func (s *QzdbSearcher) FindUint(ipInt uint32) *GeoInfo {
	if !s.hasV4 {
		return nil
	}
	rowID := s.trieWalkV4(ipInt)
	if rowID == 0 {
		return nil
	}
	return s.resolveRowID(rowID, s.groupIndex)
}

func (s *QzdbSearcher) FindV6Uint(ipInt *big.Int) *GeoInfo {
	if !s.hasV6 {
		return nil
	}
	rowID := s.trieWalkV6(ipInt)
	if rowID == 0 {
		return nil
	}
	return s.resolveRowID(rowID, s.groupIndex)
}

func (s *QzdbSearcher) FindStr(ipStr string) string {
	info := s.Find(ipStr)
	if info == nil {
		return ""
	}
	return info.ToPipe()
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
	
	tmp := make([]byte, len(s.data))
	copy(tmp, s.data)
	tmp[16] = 0
	tmp[17] = 0
	tmp[18] = 0
	tmp[19] = 0
	
	computed := crc32.ChecksumIEEE(tmp)
	return stored == computed
}

func fastParseIpV4(ip string) (uint32, bool) {
	var result, val uint32
	parts := 0
	for i := 0; i < len(ip); i++ {
		c := ip[i]
		if c >= '0' && c <= '9' {
			val = val*10 + uint32(c-'0')
			if val > 255 {
				return 0, false
			}
		} else if c == '.' {
			if i == 0 || ip[i-1] == '.' {
				return 0, false
			}
			result = (result << 8) | val
			val = 0
			parts++
		} else {
			return 0, false
		}
	}
	if parts != 3 {
		return 0, false
	}
	if len(ip) > 0 && ip[len(ip)-1] == '.' {
		return 0, false
	}
	return (result << 8) | val, true
}
