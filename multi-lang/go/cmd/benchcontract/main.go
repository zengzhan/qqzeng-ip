// Command benchcontract is the QZDB reference-compliant benchmark for Go.
//
// It implements docs/BENCH_CONTRACT.md v1.0:
//   - §3 splitmix64 reference RNG (byte-identical across all 8 languages)
//   - §4 four distributions: random / hot / sequential / real_world
//   - §5 dual-stack tri-mode: v4 / v6 / mixed(50-40-10)
//   - §6 QPS / avg / P50 / P95 / P99, cold vs hot
//   - §7 canonical JSON to multi-lang/bench_reports/go_<edition>.json
//   - §8 thread scaling 1/2/4/8/16 on a SHARED reader + 16x100k concurrency gate
//
// Before measuring anything it SELF-CHECKS parity: the FNV-1a 64 fingerprint of
// the first 1024 queries of every stream it generates must equal the value in
// multi-lang/tools/bench_vectors.json (produced by tools/bench_gen.py). A match
// proves the whole 2M-query stream is byte-identical to Python's, by
// construction — so cross-language QPS numbers are actually comparable.
//
// Env overrides:
//
//	BENCH_OPS=200000            scale ops down for a quick local run
//	BENCH_EDITIONS=std_china    comma list of editions
package main

import (
	"encoding/binary"
	"encoding/json"
	"fmt"
	"math/bits"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"qzdb_reader/qzdb"
)

// ---------------------------------------------------------------- constants

const (
	mask64      = ^uint64(0)
	masterSeed  = uint64(20260807)
	opsDefault  = 2_000_000
	warmupOps   = 1_000_000
	coldOps     = 200_000
	poolHotV4   = 4096
	poolHotV6   = 1024
	fingerprint = 1024
	latEvery    = 20

	concThreads = 16
	concOps     = 100_000 // per thread, per contract §8 (total = 1_600_000)

	mappedPrefix = uint64(0xFFFF) << 32
)

var threadConfigs = []int{1, 2, 4, 8, 16}

// distribution / mode enums (string switch in the hot loop would cost more than
// the lookup itself)
const (
	dRandom = iota
	dHot
	dSequential
	dRealWorld
)
const (
	mV4 = iota
	mV6
	mMixed
)

var distNames = []string{"random", "hot", "sequential", "real_world"}
var modeNames = []string{"v4", "v6", "mixed"}

func distID(s string) int {
	for i, n := range distNames {
		if n == s {
			return i
		}
	}
	return -1
}
func modeID(s string) int {
	for i, n := range modeNames {
		if n == s {
			return i
		}
	}
	return -1
}

// ---------------------------------------------------------------- splitmix64

type splitMix64 struct{ s uint64 }

func (r *splitMix64) next() uint64 {
	r.s += 0x9E3779B97F4A7C15
	z := r.s
	z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9
	z = (z ^ (z >> 27)) * 0x94D049BB133111EB
	return z ^ (z >> 31)
}
func (r *splitMix64) u32() uint32 { return uint32(r.next()) }
func (r *splitMix64) u128() (uint64, uint64) {
	hi := r.next()
	lo := r.next()
	return hi, lo
}

func fnv1a(b []byte, h uint64) uint64 {
	if h == 0 {
		h = 0xCBF29CE484222325
	}
	for _, c := range b {
		h ^= uint64(c)
		h *= 0x100000001B3
	}
	return h
}

func buildPools() ([]uint32, [][2]uint64) {
	p4 := &splitMix64{s: masterSeed + 1}
	pool4 := make([]uint32, poolHotV4)
	for i := range pool4 {
		pool4[i] = p4.u32()
	}
	p6 := &splitMix64{s: masterSeed + 2}
	pool6 := make([][2]uint64, poolHotV6)
	for i := range pool6 {
		hi, lo := p6.u128()
		pool6[i] = [2]uint64{hi, lo}
	}
	return pool4, pool6
}

// ---------------------------------------------------------------- generator

type query struct {
	kind uint8 // 0=v4 (ip in hi), 1=v6, 2=mapped
	hi   uint64
	lo   uint64
}

type stream struct {
	rng      splitMix64
	dist     int
	mode     int
	base4    uint32
	b6hi     uint64
	b6lo     uint64
	i        int
	pool4    []uint32
	pool6    [][2]uint64
	genV4Idx uint32
}

func newStream(dist, mode int, seed uint64, pool4 []uint32, pool6 [][2]uint64) *stream {
	s := &stream{dist: dist, mode: mode, pool4: pool4, pool6: pool6}
	s.rng.s = seed
	s.base4 = s.rng.u32()
	s.b6hi, s.b6lo = s.rng.u128()
	return s
}

func (s *stream) v4() uint32 {
	switch s.dist {
	case dRandom:
		return s.rng.u32()
	case dHot:
		return s.pool4[s.rng.u32()%poolHotV4]
	case dSequential:
		return s.base4 + uint32(s.i)
	default:
		r := s.rng.u32() % 10
		if r < 6 {
			return s.pool4[s.rng.u32()%poolHotV4]
		}
		if r < 9 {
			return s.rng.u32()
		}
		return s.base4 + uint32(s.i)
	}
}

func (s *stream) seq6() (uint64, uint64) {
	lo, carry := bits.Add64(s.b6lo, uint64(s.i), 0)
	hi := s.b6hi + carry
	return hi, lo
}

func (s *stream) v6() (uint64, uint64) {
	switch s.dist {
	case dRandom:
		return s.rng.u128()
	case dHot:
		p := s.pool6[s.rng.u32()%poolHotV6]
		return p[0], p[1]
	case dSequential:
		return s.seq6()
	default:
		r := s.rng.u32() % 10
		if r < 6 {
			p := s.pool6[s.rng.u32()%poolHotV6]
			return p[0], p[1]
		}
		if r < 9 {
			return s.rng.u128()
		}
		return s.seq6()
	}
}

func mappedFromV4(ip uint32) (uint64, uint64) { return 0, mappedPrefix | uint64(ip) }

// next produces the i-th query. Mirrors bench_gen.gen_stream() exactly,
// including the order of RNG draws (which is what makes the streams identical).
func (s *stream) next() query {
	var q query
	switch s.mode {
	case mV4:
		q = query{kind: 0, hi: uint64(s.v4())}
	case mV6:
		if s.rng.u32()%5 == 0 {
			hi, lo := mappedFromV4(s.v4())
			q = query{kind: 2, hi: hi, lo: lo}
		} else {
			hi, lo := s.v6()
			q = query{kind: 1, hi: hi, lo: lo}
		}
	default:
		switch m := s.i % 10; {
		case m < 5:
			q = query{kind: 0, hi: uint64(s.v4())}
		case m < 9:
			hi, lo := s.v6()
			q = query{kind: 1, hi: hi, lo: lo}
		default:
			hi, lo := mappedFromV4(s.v4())
			q = query{kind: 2, hi: hi, lo: lo}
		}
	}
	s.i++
	return q
}

func encQuery(q query, buf []byte) []byte {
	if q.kind == 0 {
		binary.LittleEndian.PutUint64(buf[0:8], q.hi&0xFFFFFFFF)
		return buf[:8]
	}
	binary.LittleEndian.PutUint64(buf[0:8], q.hi)
	binary.LittleEndian.PutUint64(buf[8:16], q.lo)
	return buf[:16]
}

func to16(hi, lo uint64) [16]byte {
	var b [16]byte
	binary.BigEndian.PutUint64(b[0:8], hi)
	binary.BigEndian.PutUint64(b[8:16], lo)
	return b
}

// ---------------------------------------------------------------- manifest

type streamInfo struct {
	Seed           uint64 `json:"seed"`
	Ops            int    `json:"ops"`
	First1024Sha   string `json:"first1024_sha256"`
	First1024Fnv1a string `json:"first1024_fnv1a"` // string: 64-bit > 2^53
}

type manifest struct {
	Contract   string                           `json:"contract"`
	MasterSeed uint64                           `json:"master_seed"`
	Ops        int                              `json:"ops"`
	Streams    map[string]map[string]streamInfo `json:"streams"`
}

// ---------------------------------------------------------------- metrics

// metrics carries hit_rate as a FIRST-CLASS field. Without it a QPS number is
// uninterpretable: on a regional edition most synthetic u32 queries fall
// outside any range and exit early, so a "fast" number can simply mean
// "measured the miss path". See contract §6.
type metrics struct {
	Ops     int     `json:"ops"`
	QPS     float64 `json:"qps"`
	AvgNs   float64 `json:"avg_ns"`
	P50Ns   int64   `json:"p50_ns"`
	P95Ns   int64   `json:"p95_ns"`
	P99Ns   int64   `json:"p99_ns"`
	Errors  int64   `json:"errors"`
	Hits    int64   `json:"hits"`
	HitRate float64 `json:"hit_rate"`
	Warm    string  `json:"warm,omitempty"`
	API     string  `json:"api,omitempty"`
}

func pct(v []int64, p float64) int64 {
	if len(v) == 0 {
		return 0
	}
	sort.Slice(v, func(i, j int) bool { return v[i] < v[j] })
	idx := int(float64(len(v))*p+0.9999) - 1
	if idx < 0 {
		idx = 0
	}
	if idx >= len(v) {
		idx = len(v) - 1
	}
	return v[idx]
}

// dispatch returns (hit, err). `hit` is what makes the number interpretable.
//
// Each kind goes to the entry point a real caller would use. kind 2 is an
// IPv4-mapped address (::ffff:w.x.y.z) and MUST use FindBytes, which performs
// the mapped downgrade; FindV6Uint is a pure v6 trie walk that never
// downgrades, so it would miss on every mapped query and the bench would time
// the early-exit miss path instead of a real lookup.
func dispatch(r *qzdb.QzdbReader, q query) (bool, error) {
	switch q.kind {
	case 0:
		g, err := r.FindUint(uint32(q.hi))
		return g != nil, err
	case 2:
		g, err := r.FindBytes(to16(q.hi, q.lo))
		return g != nil, err
	default:
		g, err := r.FindV6Uint(to16(q.hi, q.lo))
		return g != nil, err
	}
}

func runSingle(r *qzdb.QzdbReader, dist, mode int, seed uint64, pool4 []uint32, pool6 [][2]uint64, ops int, sample bool) metrics {
	s := newStream(dist, mode, seed, pool4, pool6)
	lat := make([]int64, 0, ops/latEvery+1)
	var errs, hits int64
	start := time.Now()
	for i := 0; i < ops; i++ {
		q := s.next()
		if sample && i%latEvery == 0 {
			t0 := time.Now()
			hit, err := dispatch(r, q)
			lat = append(lat, time.Since(t0).Nanoseconds())
			if err != nil {
				errs++
			}
			if hit {
				hits++
			}
		} else {
			hit, err := dispatch(r, q)
			if err != nil {
				errs++
			}
			if hit {
				hits++
			}
		}
	}
	el := time.Since(start).Seconds()
	return metrics{
		Ops: ops, QPS: float64(ops) / el, AvgNs: el * 1e9 / float64(ops),
		P50Ns: pct(lat, 0.50), P95Ns: pct(lat, 0.95), P99Ns: pct(lat, 0.99),
		Errors: errs, Hits: hits, HitRate: float64(hits) / float64(ops),
	}
}

// runMulti shares ONE reader across N goroutines — this is the lock-free
// snapshot concurrency assertion, not just a throughput number.
func runMulti(r *qzdb.QzdbReader, dist, mode int, seed uint64, pool4 []uint32, pool6 [][2]uint64, threads, ops int) metrics {
	per := ops / threads
	var done, errs, hits int64
	var wg sync.WaitGroup
	ready := make(chan struct{})
	for t := 0; t < threads; t++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			s := newStream(dist, mode, seed, pool4, pool6)
			<-ready
			var localErr, localHit int64
			for i := 0; i < per; i++ {
				hit, err := dispatch(r, s.next())
				if err != nil {
					localErr++
				}
				if hit {
					localHit++
				}
			}
			atomic.AddInt64(&done, int64(per))
			atomic.AddInt64(&errs, localErr)
			atomic.AddInt64(&hits, localHit)
		}()
	}
	start := time.Now()
	close(ready)
	wg.Wait()
	el := time.Since(start).Seconds()
	d := atomic.LoadInt64(&done)
	h := atomic.LoadInt64(&hits)
	return metrics{
		Ops: int(d), QPS: float64(d) / el, AvgNs: el * 1e9 / float64(max64(1, d)),
		Errors: atomic.LoadInt64(&errs), Hits: h, HitRate: float64(h) / float64(max64(1, d)),
		Warm: "hot",
	}
}

func max64(a, b int64) int64 {
	if a > b {
		return a
	}
	return b
}

// concurrencySafe is contract §8: 16 threads x 100_000 dual-stack mixed on a
// shared reader, errors==0 and done==1_600_000.
func concurrencySafe(r *qzdb.QzdbReader, seed uint64, pool4 []uint32, pool6 [][2]uint64) (bool, int64) {
	var done, errs int64
	var wg sync.WaitGroup
	for t := 0; t < concThreads; t++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			s := newStream(dHot, mMixed, seed, pool4, pool6)
			for i := 0; i < concOps; i++ {
				if _, err := dispatch(r, s.next()); err != nil {
					atomic.AddInt64(&errs, 1)
					return
				}
				atomic.AddInt64(&done, 1)
			}
		}()
	}
	wg.Wait()
	return errs == 0 && done == int64(concThreads)*concOps, done
}

// ---------------------------------------------------------------- parity

func paritySelfCheck(m *manifest, pool4 []uint32, pool6 [][2]uint64) bool {
	fmt.Print("parity self-check ... ")
	bad := 0
	buf := make([]byte, 16)
	for _, dn := range distNames {
		for _, mn := range modeNames {
			info := m.Streams[dn][mn]
			want, err := strconv.ParseUint(info.First1024Fnv1a, 10, 64)
			if err != nil {
				fmt.Printf("\n  bad manifest fnv for %s.%s: %v", dn, mn, err)
				bad++
				continue
			}
			s := newStream(distID(dn), modeID(mn), info.Seed, pool4, pool6)
			var h uint64
			for i := 0; i < fingerprint; i++ {
				h = fnv1a(encQuery(s.next(), buf), h)
			}
			if h != want {
				fmt.Printf("\n  MISMATCH %s.%s got=%d want=%d", dn, mn, h, want)
				bad++
			}
		}
	}
	if bad != 0 {
		fmt.Println("\nFAILED")
		return false
	}
	fmt.Println("OK (12/12 streams match bench_vectors.json)")
	return true
}

// ---------------------------------------------------------------- helpers

func repoRoot() string {
	d, _ := os.Getwd()
	for i := 0; i < 8; i++ {
		if st, err := os.Stat(filepath.Join(d, "multi-lang", "tools", "bench_vectors.json")); err == nil && !st.IsDir() {
			return d
		}
		p := filepath.Dir(d)
		if p == d {
			break
		}
		d = p
	}
	return ""
}

var editions = map[string][3]string{
	"std_china":  {"std", "china", "qqzeng_ip_std_china.qzdb"},
	"max_global": {"max", "global", "qqzeng_ip_max_global.qzdb"},
}

func findDB(root, edition string) string {
	e, ok := editions[edition]
	if !ok {
		return ""
	}
	for _, base := range []string{
		filepath.Join(root, "multi-lang", "test_data_202608"),
		filepath.Join(root, "test_data_202608"),
	} {
		p := filepath.Join(base, e[0], e[1], e[2])
		if _, err := os.Stat(p); err == nil {
			return p
		}
	}
	return ""
}

func fmtV4(ip uint32) string {
	var b [16]byte
	d := b[:0]
	d = strconv.AppendUint(d, uint64(ip>>24&255), 10)
	d = append(d, '.')
	d = strconv.AppendUint(d, uint64(ip>>16&255), 10)
	d = append(d, '.')
	d = strconv.AppendUint(d, uint64(ip>>8&255), 10)
	d = append(d, '.')
	d = strconv.AppendUint(d, uint64(ip&255), 10)
	return string(d)
}

func fmtV6(hi, lo uint64) string {
	g := make([]string, 8)
	for k := 0; k < 8; k++ {
		var v uint64
		if k < 4 {
			v = (hi >> (48 - 16*uint(k))) & 0xFFFF
		} else {
			v = (lo >> (48 - 16*uint(k-4))) & 0xFFFF
		}
		g[k] = strconv.FormatUint(v, 16)
	}
	return strings.Join(g, ":")
}

func cpuModel() string {
	if runtime.GOOS == "darwin" {
		if out, err := exec.Command("sysctl", "-n", "machdep.cpu.brand_string").Output(); err == nil {
			return strings.TrimSpace(string(out))
		}
	}
	return runtime.GOARCH
}

// ---------------------------------------------------------------- main

type modeReport struct {
	Cold    metrics            `json:"cold"`
	Hot     metrics            `json:"hot"`
	Threads map[string]metrics `json:"threads,omitempty"`
}

func main() {
	ops := opsDefault
	if v := os.Getenv("BENCH_OPS"); v != "" {
		if n, err := strconv.Atoi(v); err == nil {
			ops = n
		}
	}
	edList := []string{"std_china", "max_global"}
	if v := os.Getenv("BENCH_EDITIONS"); v != "" {
		edList = strings.Split(v, ",")
	}

	root := repoRoot()
	if root == "" {
		fmt.Fprintln(os.Stderr, "cannot locate repo root (multi-lang/tools/bench_vectors.json)")
		os.Exit(1)
	}
	raw, err := os.ReadFile(filepath.Join(root, "multi-lang", "tools", "bench_vectors.json"))
	if err != nil {
		fmt.Fprintln(os.Stderr, "read manifest:", err)
		os.Exit(1)
	}
	var m manifest
	if err := json.Unmarshal(raw, &m); err != nil {
		fmt.Fprintln(os.Stderr, "parse manifest:", err)
		os.Exit(1)
	}

	pool4, pool6 := buildPools()
	if !paritySelfCheck(&m, pool4, pool6) {
		os.Exit(1)
	}

	reports := filepath.Join(root, "multi-lang", "bench_reports")
	_ = os.MkdirAll(reports, 0o755)

	for _, edition := range edList {
		db := findDB(root, edition)
		if db == "" {
			fmt.Printf("[SKIP] %s: db not found\n", edition)
			continue
		}
		r, err := qzdb.Open(db, 0, false)
		if err != nil {
			fmt.Printf("[SKIP] %s: open failed: %v\n", edition, err)
			continue
		}
		st, _ := os.Stat(db)
		fmt.Printf("\nedition %s: %s (%d bytes)\n", edition, db, st.Size())

		safe, cdone := concurrencySafe(r, m.Streams["hot"]["mixed"].Seed, pool4, pool6)
		fmt.Printf("  concurrency_safe(%dx%dk): %v (done=%d)\n", concThreads, concOps/1000, safe, cdone)

		distOut := map[string]map[string]modeReport{}
		for _, dn := range distNames {
			distOut[dn] = map[string]modeReport{}
			di := distID(dn)
			for _, mn := range modeNames {
				mi := modeID(mn)
				seed := m.Streams[dn][mn].Seed

				cold := runSingle(r, di, mi, seed, pool4, pool6, minInt(ops, coldOps), true)
				cold.Warm = "cold"
				runSingle(r, di, mi, seed, pool4, pool6, minInt(ops, warmupOps), false)
				hot := runSingle(r, di, mi, seed, pool4, pool6, ops, true)
				hot.Warm = "hot"

				th := map[string]metrics{}
				for _, tc := range threadConfigs {
					th[strconv.Itoa(tc)] = runMulti(r, di, mi, seed, pool4, pool6, tc, ops)
				}
				distOut[dn][mn] = modeReport{Cold: cold, Hot: hot, Threads: th}

				fmt.Printf("  %-11s.%-6s hot QPS=%12.0f p50=%6dns p99=%7dns  1T=%11.0f 16T=%12.0f (%.1fx) err=%d hit=%.1f%%\n",
					dn, mn, hot.QPS, hot.P50Ns, hot.P99Ns,
					th["1"].QPS, th["16"].QPS, th["16"].QPS/th["1"].QPS, hot.Errors, hot.HitRate*100)
			}
		}

		// string round-trip on hot.mixed — isolates parse cost from decode cost
		seed := m.Streams["hot"]["mixed"].Seed
		s := newStream(dHot, mMixed, seed, pool4, pool6)
		lat := make([]int64, 0, ops/latEvery+1)
		var serr int64
		start := time.Now()
		for i := 0; i < ops; i++ {
			q := s.next()
			var str string
			if q.kind == 0 {
				str = fmtV4(uint32(q.hi))
			} else {
				str = fmtV6(q.hi, q.lo)
			}
			if i%latEvery == 0 {
				t0 := time.Now()
				if _, e := r.Find(str); e != nil {
					serr++
				}
				lat = append(lat, time.Since(t0).Nanoseconds())
			} else if _, e := r.Find(str); e != nil {
				serr++
			}
		}
		el := time.Since(start).Seconds()
		strRT := metrics{
			API: "string", Ops: ops, QPS: float64(ops) / el, AvgNs: el * 1e9 / float64(ops),
			P50Ns: pct(lat, 0.50), P95Ns: pct(lat, 0.95), P99Ns: pct(lat, 0.99),
			Errors: serr, Warm: "hot",
		}
		fmt.Printf("  %-11s.%-6s STRING round-trip QPS=%12.0f p99=%7dns (errors are expected: unassigned space)\n",
			"hot", "mixed", strRT.QPS, strRT.P99Ns)

		report := map[string]any{
			"contract":    "QZDB_BENCH_CONTRACT v1.0",
			"language":    "go",
			"sdk_version": "multi-lang/go (module qzdb_reader)",
			"timestamp":   time.Now().Format(time.RFC3339),
			"seed":        masterSeed,
			"db": map[string]any{
				"path": db, "edition": edition, "bytes": st.Size(), "hash": "crc32:n/a",
			},
			"environment": map[string]any{
				"cpu":            cpuModel(),
				"cores":          runtime.NumCPU(),
				"ram_gb":         nil,
				"os":             runtime.GOOS + " " + runtime.GOARCH,
				"runtime":        runtime.Version(),
				"compiler":       "gc " + runtime.Version(),
				"bench_contract": "v1.0",
				"note":           "Native goroutines: thread scaling 1/2/4/8/16 shares ONE reader; run `go test -race` separately as the race-detector evidence required by contract 8.",
			},
			"distributions":    distOut,
			"string_roundtrip": map[string]any{"hot": map[string]any{"mixed": strRT}},
			"concurrency_safe": safe,
			"concurrency_done": cdone,
			"concurrency_spec": fmt.Sprintf("%d threads x %d ops shared reader", concThreads, concOps),
		}
		out := filepath.Join(reports, "go_"+edition+".json")
		b, _ := json.MarshalIndent(report, "", "  ")
		if err := os.WriteFile(out, b, 0o644); err != nil {
			fmt.Fprintln(os.Stderr, "write report:", err)
		} else {
			fmt.Printf("  wrote %s\n", out)
		}
	}
}

func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}
