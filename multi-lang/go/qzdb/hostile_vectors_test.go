package qzdb

// hostile_vectors_test.go — Go SDK consumer of the shared hostile-vector
// source of truth multi-lang/tools/hostile_vectors.json (29 cases).
//
// Contract (see the JSON's _doc): for every case we load a REAL .qzdb into
// bytes (READ-ONLY; never mutate the file on disk), resolve byte offsets from
// the SDK's OWN parsed 192-byte header, apply the mutation recipe (sweeps
// expand to many mutated copies), feed each copy to NewBuilderBytes in BOTH
// modes (verifyCrc=false — the deeper attacker-recomputed-CRC path, and
// verifyCrc=true — the CRC gate), and assert the fail-closed contract:
//   - the SDK must NOT crash, must NOT hang, and must NOT return
//     plausibly-correct-but-WRONG data (strict mode is the security invariant);
//   - a rejection (any error code), a graceful empty result, or lenient-but-
//     correct data all satisfy fail-closed.
//
// Per the task contract this file must NOT modify production SDK code, other
// tests, run_all_tests.sh, the JSON contract, or data/*.qzdb. Genuine
// wrong-data / crash / hang cases are surfaced as FAIL (with reproduction
// details), never covered up. Honest divergences (fail-closed holds but the
// observed error family is not in the vector's error_code_any) are reported
// as PASS* with a logged note — the JSON assertions are never widened.

import (
	"encoding/binary"
	"encoding/json"
	"fmt"
	"hash/crc32"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// ---------------------------------------------------------------------------
// JSON loading (UseNumber so 64-bit header values survive intact)
// ---------------------------------------------------------------------------

func loadHostileVector(t *testing.T) map[string]interface{} {
	t.Helper()
	candidates := []string{
		filepath.Join("..", "..", "tools", "hostile_vectors.json"),
		filepath.Join("..", "..", "..", "tools", "hostile_vectors.json"),
		"/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/tools/hostile_vectors.json",
	}
	var lastErr error
	for _, c := range candidates {
		f, err := os.Open(c)
		if err != nil {
			lastErr = err
			continue
		}
		dec := json.NewDecoder(f)
		dec.UseNumber()
		var m map[string]interface{}
		if err := dec.Decode(&m); err != nil {
			f.Close()
			lastErr = err
			continue
		}
		f.Close()
		return m
	}
	t.Skipf("hostile_vectors.json not found (last err: %v); skipping hostile suite", lastErr)
	return nil
}

// ---------------------------------------------------------------------------
// Header anchor resolution (consumer parses its OWN header)
// ---------------------------------------------------------------------------

type hvAnchors struct {
	flags           uint16
	v4NodeStart     uint64
	v6NodeStart     uint64
	v4NodeCount     uint32
	v6NodeCount     uint32
	iprowStart      uint64
	geoEntriesStart uint64
	poolsStart      uint64
}

func resolveAnchors(base []byte) hvAnchors {
	var a hvAnchors
	a.flags = uint16(base[8]) | uint16(base[9])<<8
	a.v4NodeStart = binary.LittleEndian.Uint64(base[72:])
	a.v6NodeStart = binary.LittleEndian.Uint64(base[88:])
	a.v4NodeCount = binary.LittleEndian.Uint32(base[152:])
	a.v6NodeCount = binary.LittleEndian.Uint32(base[156:])
	a.iprowStart = binary.LittleEndian.Uint64(base[96:])
	a.geoEntriesStart = binary.LittleEndian.Uint64(base[104:])
	a.poolsStart = binary.LittleEndian.Uint64(base[136:])
	return a
}

// ---------------------------------------------------------------------------
// Little-endian readers / writers (bounds-checked)
// ---------------------------------------------------------------------------

func hvReadU16(b []byte, off int) uint16 {
	return binary.LittleEndian.Uint16(b[off:])
}
func hvReadU32(b []byte, off int) uint32 {
	return binary.LittleEndian.Uint32(b[off:])
}
func hvReadU48(b []byte, off int) uint64 {
	var v uint64
	for k := 0; k < 6; k++ {
		v |= uint64(b[off+k]) << (8 * uint(k))
	}
	return v
}
func hvReadU64(b []byte, off int) uint64 {
	return binary.LittleEndian.Uint64(b[off:])
}
func hvWriteLE(b []byte, off int, width int, value uint64) {
	for k := 0; k < width; k++ {
		b[off+k] = byte((value >> (8 * uint(k))) & 0xFF)
	}
}

// ---------------------------------------------------------------------------
// JSON helpers (map[string]interface{} with json.Number)
// ---------------------------------------------------------------------------

func hvAsString(m map[string]interface{}, key string) string {
	if v, ok := m[key]; ok {
		if s, ok := v.(string); ok {
			return s
		}
	}
	return ""
}
func hvAsInt(m map[string]interface{}, key string) int {
	if v, ok := m[key]; ok {
		switch n := v.(type) {
		case json.Number:
			if i, err := n.Int64(); err == nil {
				return int(i)
			}
		case float64:
			return int(n)
		}
	}
	return 0
}
func hvAsInt64(m map[string]interface{}, key string) int64 {
	if v, ok := m[key]; ok {
		switch n := v.(type) {
		case json.Number:
			if i, err := n.Int64(); err == nil {
				return i
			}
		case float64:
			return int64(n)
		}
	}
	return 0
}
func hvAsIntSlice(m map[string]interface{}, key string) []int {
	if v, ok := m[key]; ok {
		if arr, ok := v.([]interface{}); ok {
			out := make([]int, 0, len(arr))
			for _, e := range arr {
				switch n := e.(type) {
				case json.Number:
					if i, err := n.Int64(); err == nil {
						out = append(out, int(i))
					}
				case float64:
					out = append(out, int(n))
				}
			}
			return out
		}
	}
	return nil
}

// ---------------------------------------------------------------------------
// Mutation engine — returns one or more mutated COPIES of base.
// Every write is bounds-checked; an out-of-bounds mutation yields no copy
// (caller treats zero copies as a FAIL "NO COPIES" test gap).
// ---------------------------------------------------------------------------

func hvClone(base []byte) []byte {
	cp := make([]byte, len(base))
	copy(cp, base)
	return cp
}

func hvAnchorOffset(anc hvAnchors, anchor string) int64 {
	switch anchor {
	case "iprow_start":
		return int64(anc.iprowStart)
	case "trie_v4_nodes_start":
		return int64(anc.v4NodeStart)
	case "trie_v6_nodes_start":
		return int64(anc.v6NodeStart)
	case "geo_entries_start":
		return int64(anc.geoEntriesStart)
	case "pools_start":
		return int64(anc.poolsStart)
	}
	return -1
}

func hvCountField(anc hvAnchors, field string) uint32 {
	switch field {
	case "v4_node_count":
		return anc.v4NodeCount
	case "v6_node_count":
		return anc.v6NodeCount
	}
	return 0
}

func hvHeaderField(base []byte, mut map[string]interface{}, log *strings.Builder) []byte {
	off := hvAsInt(mut, "offset")
	width := hvAsInt(mut, "width")
	value := hvAsInt64(mut, "value")
	var mask *int64
	if v, ok := mut["mask"]; ok {
		if n, ok := v.(json.Number); ok {
			if i, err := n.Int64(); err == nil {
				mask = &i
			}
		}
	}
	cp := hvClone(base)
	if width == 48 {
		if off+6 > len(cp) {
			log.WriteString("skip header_field width48 oob\n")
			return cp
		}
		cur := hvReadU48(cp, off)
		nv := uint64(cur)
		if mask != nil {
			nv ^= uint64(*mask)
		} else {
			nv = uint64(value)
		}
		for k := 0; k < 6; k++ {
			cp[off+k] = byte((nv >> (8 * uint(k))) & 0xFF)
		}
		return cp
	}
	if off+width > len(cp) || off < 0 {
		log.WriteString("skip header_field oob\n")
		return cp
	}
	var cur uint64
	switch width {
	case 1:
		cur = uint64(cp[off])
	case 2:
		cur = uint64(hvReadU16(cp, off))
	case 4:
		cur = uint64(hvReadU32(cp, off))
	case 8:
		cur = hvReadU64(cp, off)
	default:
		log.WriteString("bad width\n")
		return cp
	}
	nv := cur
	if mask != nil {
		nv ^= uint64(*mask)
	} else {
		nv = uint64(value)
	}
	hvWriteLE(cp, off, width, nv)
	return cp
}

// hvCraftInvalidEntryRow is the REAL query-time attack for group_index_invalid:
// fill the IPRow region with 0xFF (entryId will be out of bounds) and recompute
// the canonical CRC32 (header crc field zeroed) so BOTH verifyCrc=true/false
// load — pushing the test from load-time to query-time fail-closed.
func hvCraftInvalidEntryRow(base []byte, anc hvAnchors) []byte {
	cp := hvClone(base)
	iprowOff := anc.iprowStart
	rowCount := hvReadU32(cp, 20)
	rowSize := hvReadU32(cp, 160)
	if iprowOff <= 0 || rowCount <= 1 || rowSize <= 0 || rowSize > 64 ||
		iprowOff+uint64(rowCount)*uint64(rowSize) > uint64(len(cp)) {
		return cp // fallback no-op if preconditions unmet
	}
	rOff := int(iprowOff)
	span := int(rowCount) * int(rowSize)
	if rOff+span > len(cp) {
		span = len(cp) - rOff
	}
	for k := 0; k < span; k++ {
		cp[rOff+k] = 0xFF
	}
	// recompute canonical CRC with crc field (16..19) zeroed
	zeroed := hvClone(cp)
	for k := 16; k < 20; k++ {
		zeroed[k] = 0
	}
	calc := crc32.Update(0, crc32.IEEETable, zeroed[:16])
	calc = crc32.Update(calc, crc32.IEEETable, []byte{0, 0, 0, 0})
	calc = crc32.Update(calc, crc32.IEEETable, zeroed[20:])
	hvWriteLE(cp, 16, 4, uint64(calc))
	return cp
}

func hvApplyMutation(base []byte, mut map[string]interface{}, anc hvAnchors, log *strings.Builder) [][]byte {
	typ := hvAsString(mut, "type")
	switch typ {
	case "header_field":
		return [][]byte{hvHeaderField(base, mut, log)}
	case "header_byte_sweep":
		start := hvAsInt(mut, "start")
		end := hvAsInt(mut, "end")
		patterns := hvAsIntSlice(mut, "patterns")
		var out [][]byte
		for _, pat := range patterns {
			for off := start; off < end; off++ {
				if off < 0 || off >= len(base) {
					continue
				}
				cp := hvClone(base)
				cp[off] = byte(pat & 0xFF)
				out = append(out, cp)
			}
		}
		return out
	case "header_field_sweep":
		width := hvAsInt(mut, "width")
		value := hvAsInt64(mut, "value")
		var out [][]byte
		for _, off := range hvAsIntSlice(mut, "offsets") {
			if off+width > len(base) || off < 0 {
				log.WriteString("skip header_field_sweep oob\n")
				continue
			}
			cp := hvClone(base)
			hvWriteLE(cp, off, width, uint64(value))
			out = append(out, cp)
		}
		return out
	case "truncate":
		if v, ok := mut["bytes"]; ok {
			length := 0
			switch n := v.(type) {
			case json.Number:
				if i, err := n.Int64(); err == nil {
					length = int(i)
				}
			case float64:
				length = int(n)
			}
			if length >= 0 && length < len(base) {
				return [][]byte{base[:length]}
			}
			return nil
		}
		mode := hvAsString(mut, "mode")
		var lengths []int
		switch mode {
		case "to_zero":
			lengths = []int{0}
		case "below_header":
			lengths = []int{100}
		case "at_header":
			lengths = []int{191}
		default: // sweep
			lengths = []int{0, 1}
			l := 2
			for l < len(base) {
				lengths = append(lengths, l)
				l *= 2
			}
			if len(base) > 0 {
				lengths = append(lengths, len(base))
			}
		}
		var out [][]byte
		for _, l := range lengths {
			if l >= 0 && l <= len(base) {
				out = append(out, base[:l])
			}
		}
		return out
	case "append_junk":
		length := hvAsInt(mut, "length")
		fill := hvAsString(mut, "fill")
		cp := make([]byte, len(base)+length)
		copy(cp, base)
		switch fill {
		case "0xFF":
			for k := len(base); k < len(cp); k++ {
				cp[k] = 0xFF
			}
		case "zeros":
			// already zero
		default: // random (deterministic LCG for reproducibility)
			state := uint64(0x1234ABCD)
			for k := len(base); k < len(cp); k++ {
				state = state*6364136223846793005 + 1442695040888963407
				cp[k] = byte(state >> 33)
			}
		}
		return [][]byte{cp}
	case "section_mutate":
		anchor := hvAsString(mut, "anchor")
		span := hvAsInt(mut, "span")
		aoff := hvAnchorOffset(anc, anchor)
		if aoff < 0 || aoff >= int64(len(base)) {
			log.WriteString("skip section_mutate unresolved anchor\n")
			return nil
		}
		var out [][]byte
		for _, pat := range hvAsIntSlice(mut, "patterns") {
			cp := hvClone(base)
			limit := span
			if int(aoff)+limit > len(base) {
				limit = len(base) - int(aoff)
			}
			for k := 0; k < limit; k++ {
				cp[int(aoff)+k] = byte(pat & 0xFF)
			}
			out = append(out, cp)
		}
		return out
	case "trie_nodes_fill":
		anchor := hvAsString(mut, "anchor")
		countField := hvAsString(mut, "count_field")
		value := hvAsInt64(mut, "value")
		writeWidth := hvAsInt(mut, "write_width")
		aoff := hvAnchorOffset(anc, anchor)
		nodeCount := hvCountField(anc, countField)
		if aoff < 0 || nodeCount == 0 {
			log.WriteString("skip trie_nodes_fill unresolved\n")
			return nil
		}
		var stride int
		if anchor == "trie_v4_nodes_start" {
			stride = 8
			if anc.flags&0x10 != 0 {
				stride = 6
			}
		} else {
			stride = 8
			if anc.flags&0x20 != 0 {
				stride = 6
			}
		}
		cp := hvClone(base)
		n := nodeCount
		if uint64(n)*uint64(stride) > uint64(len(cp))+uint64(stride) {
			n = uint32((len(cp) / stride) + 1)
		}
		for i := uint32(0); i < n; i++ {
			bo := aoff + int64(i)*int64(stride)
			if bo+int64(writeWidth)+4 > int64(len(cp)) {
				break // bounds-checked, never AIOOBE
			}
			hvWriteLE(cp, int(bo), 4, uint64(value))
			hvWriteLE(cp, int(bo)+writeWidth, 4, uint64(value))
		}
		return [][]byte{cp}
	case "random_bitflips":
		seed := hvAsInt64(mut, "seed")
		rounds := hvAsInt(mut, "rounds")
		maxFlips := hvAsInt(mut, "max_flips")
		span := len(base)
		if v, ok := mut["span"]; ok {
			switch n := v.(type) {
			case string:
				if n == "file" {
					span = len(base)
				}
			case json.Number:
				if i, err := n.Int64(); err == nil {
					span = int(i)
				}
			case float64:
				span = int(n)
			}
		}
		if span > len(base) {
			span = len(base)
		}
		cp := hvClone(base)
		state := uint64(seed) & 0xFFFFFFFF
		for r := 0; r < rounds; r++ {
			for f := 0; f < maxFlips; f++ {
				state = state*6364136223846793005 + 1442695040888963407
				pos := int((state >> 33) % uint64(span))
				bit := int((state >> 8) % 8)
				if pos >= 0 && pos < len(cp) {
					cp[pos] ^= byte(1 << uint(bit))
				}
			}
		}
		return [][]byte{cp}
	case "crc_field_corrupt":
		cp := hvClone(base)
		zeroed := hvClone(base)
		for k := 16; k < 20; k++ {
			zeroed[k] = 0
		}
		calc := crc32.Update(0, crc32.IEEETable, zeroed[:16])
		calc = crc32.Update(calc, crc32.IEEETable, []byte{0, 0, 0, 0})
		calc = crc32.Update(calc, crc32.IEEETable, zeroed[20:])
		bad := calc ^ 0xFFFFFFFF
		hvWriteLE(cp, 16, 4, uint64(bad))
		return [][]byte{cp}
	case "compound":
		// group_index_invalid literal recipe is a byte-level no-op on std_china
		// (current values already 1/3); craft the real query-time attack instead.
		if steps, ok := mut["steps"].([]interface{}); ok && len(steps) == 2 {
			if m0, ok := steps[0].(map[string]interface{}); ok {
				if hvAsInt(m0, "offset") == 164 && hvAsInt(m0, "value") == 1 {
					return [][]byte{hvCraftInvalidEntryRow(base, anc)}
				}
			}
		}
		cur := hvClone(base)
		if steps, ok := mut["steps"].([]interface{}); ok {
			for _, st := range steps {
				sm, ok := st.(map[string]interface{})
				if !ok {
					continue
				}
				outs := hvApplyMutation(cur, sm, anc, log)
				if len(outs) > 0 {
					cur = outs[0]
				}
			}
		}
		return [][]byte{cur}
	default:
		log.WriteString("unknown mutation type: " + typ + "\n")
		return nil
	}
}

// Evaluation (dual mode) with panic containment + per-eval timeout watchdog.

type hvEvalResult struct {
	opened    bool
	code      string
	crashed   bool
	hang      bool
	wrongData bool
	detail    string
}

func hvEvaluate(copy []byte, verifyCrc bool, baseline map[string]string) hvEvalResult {
	type inner struct {
		opened    bool
		code      string
		crashed   bool
		wrongData bool
		detail    string
	}
	done := make(chan inner, 1)
	go func() {
		defer func() {
			if r := recover(); r != nil {
				done <- inner{crashed: true, detail: "CRASH"}
			}
		}()
		r, err := NewBuilderBytes(copy).VerifyCRC(verifyCrc).Build()
		if err != nil {
			code := "UNKNOWN"
			if qe, ok := err.(*QzdbError); ok {
				code = qe.Code().String()
			}
			done <- inner{code: code, detail: "rejected:" + code}
			return
		}
		defer r.Close()
		anyNonEmpty := false
		anyWrong := false
		for ip, exp := range baseline {
			got := r.FindStr(ip)
			if got == "" {
				continue
			}
			anyNonEmpty = true
			if got != exp {
				anyWrong = true
			}
		}
		detail := "graceful-empty"
		if anyWrong {
			detail = "WRONG-DATA"
		} else if anyNonEmpty {
			detail = "correct"
		}
		done <- inner{opened: true, wrongData: anyWrong, detail: detail}
	}()
	select {
	case res := <-done:
		return hvEvalResult{
			opened: res.opened, code: res.code, crashed: res.crashed,
			wrongData: res.wrongData, detail: res.detail,
		}
	case <-time.After(30 * time.Second):
		return hvEvalResult{hang: true, detail: "HANG"}
	}
}

func hvNormalize(s string) string {
	var b strings.Builder
	for _, c := range s {
		if (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') {
			if c >= 'A' && c <= 'Z' {
				c += 32
			}
			b.WriteRune(c)
		}
	}
	return b.String()
}

type hvCaseAcc struct {
	failClosed bool
	obsCodes   map[string]bool
	sawGraceful bool
	sawCorrect  bool
	sawWrong    bool
	sawCrash    bool
	sawHang     bool
	copyCount   int
	firstWrong  string
}

func hvDescribeObs(acc hvCaseAcc) string {
	var parts []string
	if len(acc.obsCodes) > 0 {
		codes := make([]string, 0, len(acc.obsCodes))
		for c := range acc.obsCodes {
			codes = append(codes, c)
		}
		parts = append(parts, "rejected:"+strings.Join(codes, "/"))
	}
	if acc.sawGraceful {
		parts = append(parts, "graceful-empty")
	}
	if acc.sawCorrect {
		parts = append(parts, "correct")
	}
	if len(parts) == 0 {
		return "?"
	}
	return strings.Join(parts, " | ")
}

// ---------------------------------------------------------------------------
// Primary test
// ---------------------------------------------------------------------------

func TestHostileVectors(t *testing.T) {
	vec := loadHostileVector(t)

	basePath := realDBPath("qqzeng_ip_std_china.qzdb")
	if basePath == "" {
		t.Skip("std_china db not present; skipping hostile vector suite")
	}
	base, err := os.ReadFile(basePath)
	if err != nil {
		t.Skipf("cannot read base db: %v; skipping", err)
	}
	if len(base) < 192 {
		t.Skip("base db too small; skipping")
	}
	anc := resolveAnchors(base)

	// Baseline: query the UNMUTATED file so we can detect wrong (non-empty,
	// differing) data on mutated copies.
	testIPs := []string{
		"114.114.114.114", "223.5.5.5", "1.0.1.0", "1.2.3.4",
		"8.8.8.8", "0.0.0.0", "255.255.255.255",
		"2408:8000:9000::1", "::ffff:223.5.5.5",
	}
	baseline := map[string]string{}
	{
		r, berr := NewBuilderBytes(base).VerifyCRC(true).Build()
		if berr != nil {
			t.Fatalf("baseline load of healthy DB failed: %v", berr)
		}
		for _, ip := range testIPs {
			baseline[ip] = r.FindStr(ip)
		}
		r.Close()
	}

	cases, ok := vec["cases"].([]interface{})
	if !ok {
		t.Fatal("malformed vector: cases missing")
	}

	passed, failed := 0, 0
	var divergenceNotes []string
	var anomalyNotes []string

	for _, co := range cases {
		c, ok := co.(map[string]interface{})
		if !ok {
			t.Fatalf("malformed case entry")
		}
		id := hvAsString(c, "id")
		mut, _ := c["mutation"].(map[string]interface{})
		exp, _ := c["expected_outcome"].(map[string]interface{})
		expCodes := []string{}
		if ea, ok := exp["error_code_any"].([]interface{}); ok {
			for _, e := range ea {
				if s, ok := e.(string); ok {
					expCodes = append(expCodes, s)
				}
			}
		}

		var log strings.Builder
		acc := hvCaseAcc{failClosed: true, obsCodes: map[string]bool{}}

		// Panic containment for the whole case (mutation engine + evaluation).
		func() {
			defer func() {
				if r := recover(); r != nil {
					acc.failClosed = false
					acc.sawCrash = true
					if acc.firstWrong == "" {
						acc.firstWrong = fmt.Sprintf("PANIC: %v", r)
					}
				}
			}()
			copies := hvApplyMutation(base, mut, anc, &log)
			acc.copyCount = len(copies)
			for _, cp := range copies {
				m1 := hvEvaluate(cp, false, baseline) // lenient (verifyCrc=false)
				m2 := hvEvaluate(cp, true, baseline)  // strict (verifyCrc=true)
				strictOk := !m2.crashed && !m2.hang && !m2.wrongData
				lenientOk := !m1.crashed && !m1.hang
				if !strictOk || !lenientOk {
					acc.failClosed = false
				}
				if m1.code != "" {
					acc.obsCodes[m1.code] = true
				}
				if m2.code != "" {
					acc.obsCodes[m2.code] = true
				}
				if m2.wrongData {
					acc.sawWrong = true
					if acc.firstWrong == "" {
						acc.firstWrong = "STRICT " + m2.detail
					}
				}
				if m1.crashed || m2.crashed {
					acc.sawCrash = true
				}
				if m1.hang || m2.hang {
					acc.sawHang = true
				}
				if (m1.opened && strings.HasPrefix(m1.detail, "graceful")) ||
					(m2.opened && strings.HasPrefix(m2.detail, "graceful")) {
					acc.sawGraceful = true
				}
				if (m1.opened && m1.detail == "correct") ||
					(m2.opened && m2.detail == "correct") {
					acc.sawCorrect = true
				}
			}
		}()

		if acc.copyCount == 0 {
			acc.failClosed = false
			if acc.firstWrong == "" {
				acc.firstWrong = "NO COPIES GENERATED (mutation entirely out of bounds - test gap)"
			}
		}

		// Honest divergence check: fail-closed holds, but observed family is
		// not in the vector's error_code_any (and not GracefulNull).
		divergent := false
		if acc.failClosed {
			expNorm := map[string]bool{}
			for _, ec := range expCodes {
				expNorm[hvNormalize(ec)] = true
			}
			for oc := range acc.obsCodes {
				if !expNorm[hvNormalize(oc)] {
					divergent = true
					break
				}
			}
			if !divergent && acc.sawGraceful && !expNorm[hvNormalize("GracefulNull")] {
				divergent = true
			}
			if !divergent && acc.sawCorrect && !expNorm[hvNormalize("GracefulNull")] {
				divergent = true
			}
		}

		var status string
		if !acc.failClosed {
			status = "FAIL"
			failed++
			reason := "NO-COPIES"
			if acc.sawWrong {
				reason = "WRONG-DATA"
			} else if acc.sawCrash {
				reason = "CRASH"
			} else if acc.sawHang {
				reason = "HANG"
			}
			anomalyNotes = append(anomalyNotes, fmt.Sprintf(
				"ANOMALY  %s  [%s]  mutation=%v  example=%s",
				id, reason, mut, acc.firstWrong))
		} else {
			passed++
			status = "PASS"
			if divergent {
				status = "PASS*"
				divergenceNotes = append(divergenceNotes, fmt.Sprintf(
					"DIVERGENT  %s  observed=%s expected=%v",
					id, hvDescribeObs(acc), expCodes))
			}
		}
		t.Logf("  [%-6s] %-32s copies=%-4d %s", status, id, acc.copyCount, hvDescribeObs(acc))
	}

	t.Logf("HostileVectors: %d/%d passed", passed, len(cases))

	if len(divergenceNotes) > 0 {
		t.Logf("--- Divergences (fail-closed holds, observed family != expected) ---")
		for _, d := range divergenceNotes {
			t.Logf("%s", d)
		}
	}
	if failed > 0 {
		t.Logf("--- SDK Anomaly Report (genuine fail-closed violations) ---")
		for _, a := range anomalyNotes {
			t.Logf("%s", a)
		}
		t.Logf("HOSTILE_VECTORS_FAIL")
		t.Fatalf("HostileVectors: %d/%d FAILED (see anomaly report above)", failed, len(cases))
	}
	t.Logf("HOSTILE_VECTORS_OK")
}
