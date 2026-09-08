package qzdb

import "encoding/binary"

// BatchResult 批量查询的单条结果（保留三态语义）。
type BatchResult struct {
	IP      string
	GeoInfo *GeoInfo
	Error   error
}

// batchEntry 把单条查询结果收敛为批量三态：Find/FindFields 的 (nil, nil)
// 混合了「非法 IP」与「未命中」（契约 §4 Go 行），批量路径必须可区分——
// 非法 IP 标记 Error，未命中保持 (nil, nil)。仅对空结果付出一次再解析。
func batchEntry(ip string, g *GeoInfo, err error) BatchResult {
	if err == nil && g == nil {
		if _, ok := fastParseIp(ip); !ok {
			err = newErr(ErrCodeInvalidParam, "invalid ip: "+ip)
		}
	}
	return BatchResult{IP: ip, GeoInfo: g, Error: err}
}

// LookupBatch 批量查询别名（对标 FindBatch）。
func (r *QzdbReader) LookupBatch(ips []string) []BatchResult {
	return r.FindBatch(ips)
}

// FindBatch 流水线交织批量查询；逐条保留三态语义（命中、未命中、非法）。ips 为 nil 返回空。
func (r *QzdbReader) FindBatch(ips []string) []BatchResult {
	if ips == nil {
		return nil
	}
	s := r.snapshot()
	if s == nil {
		out := make([]BatchResult, len(ips))
		for i, ip := range ips {
			out[i] = BatchResult{IP: ip, GeoInfo: nil, Error: ErrClosed}
		}
		return out
	}

	out := make([]BatchResult, len(ips))
	const batchWidth = 4
	n := len(ips)

	for base := 0; base < n; base += batchWidth {
		chunk := n - base
		if chunk > batchWidth {
			chunk = batchWidth
		}

		var (
			v4s      [batchWidth]uint32
			isV4     [batchWidth]bool
			v6s      [batchWidth][16]byte
			isV6     [batchWidth]bool
			rowIDs   [batchWidth]uint32
			errs     [batchWidth]error
			resolved [batchWidth]bool

			// Trie walk state for v4
			active [batchWidth]bool
			idx    [batchWidth]uint32
			suffix [batchWidth]uint32
		)

		// Stage 1: Fast parse all items in chunk (zero-alloc)
		for j := 0; j < chunk; j++ {
			ip := ips[base+j]
			if ip == "" {
				errs[j] = newErr(ErrCodeInvalidParam, "invalid ip: ")
				resolved[j] = true
				continue
			}
			res, ok := fastParseIp(ip)
			if !ok {
				errs[j] = newErr(ErrCodeInvalidParam, "invalid ip: "+ip)
				resolved[j] = true
			} else if res.isV4 {
				isV4[j] = true
				v4s[j] = res.v4
			} else {
				isV6[j] = true
				v6s[j] = res.v6
			}
		}

		// Stage 2: Jump table lookup for v4 items
		for j := 0; j < chunk; j++ {
			if isV4[j] {
				if !s.hasV4 || s.offV4Jump <= 0 || s.offV4Jump+uint64(v4s[j]>>16)*4+4 > uint64(len(s.data)) {
					resolved[j] = true
					rowIDs[j] = 0
				} else {
					ptr := binary.LittleEndian.Uint32(s.data[s.offV4Jump+uint64(v4s[j]>>16)*4:])
					if ptr == 0 {
						resolved[j] = true
						rowIDs[j] = 0
					} else if ptr&SENTINEL != 0 {
						resolved[j] = true
						rowIDs[j] = ptr & SENTINEL_MASK_31
					} else {
						idx[j] = ptr & SENTINEL_MASK_31
						suffix[j] = (v4s[j] & 0xFFFF) << 16
						active[j] = true
					}
				}
			}
		}

		// Stage 3: Interleaved Trie walk for active v4 items
		mask := s.nodeMask(true)
		for steps := 0; steps < maxTrieWalkSteps; steps++ {
			anyActive := false
			for j := 0; j < chunk; j++ {
				if active[j] {
					child := s.readV4Child(idx[j], (suffix[j]>>31)&1)
					if child == 0 {
						active[j] = false
						resolved[j] = true
						rowIDs[j] = 0
					} else if s.isLeaf(child, true) {
						active[j] = false
						resolved[j] = true
						rowIDs[j] = s.leafValue(child, true)
					} else {
						idx[j] = child & mask
						suffix[j] <<= 1
						anyActive = true
					}
				}
			}
			if !anyActive {
				break
			}
		}

		for j := 0; j < chunk; j++ {
			if active[j] {
				errs[j] = ErrCorrupted
				resolved[j] = true
			}
		}

		// Stage 4: Process IPv6 items in chunk
		for j := 0; j < chunk; j++ {
			if isV6[j] && !resolved[j] {
				rowID, err := s.trieWalkV6(v6s[j])
				rowIDs[j] = rowID
				errs[j] = err
				resolved[j] = true
			}
		}

		// Stage 5: Geo resolution with zero-alloc batch entries
		for j := 0; j < chunk; j++ {
			ip := ips[base+j]
			if errs[j] != nil {
				out[base+j] = BatchResult{IP: ip, GeoInfo: nil, Error: errs[j]}
			} else if rowIDs[j] == 0 {
				out[base+j] = BatchResult{IP: ip, GeoInfo: nil, Error: nil}
			} else {
				func() {
					defer func() {
						if rec := recover(); rec != nil {
							out[base+j] = BatchResult{IP: ip, GeoInfo: nil, Error: ErrCorrupted}
						}
					}()
					g := s.extractGeoInfo(rowIDs[j])
					out[base+j] = BatchResult{IP: ip, GeoInfo: g, Error: nil}
				}()
			}
		}
	}
	return out
}

// FindBatchFields 顺序批量字段投影查询（流水线交织）。
func (r *QzdbReader) FindBatchFields(ips []string, fields []string) []BatchResult {
	if ips == nil {
		return nil
	}
	if len(fields) == 0 {
		return r.FindBatch(ips)
	}
	s := r.snapshot()
	if s == nil {
		out := make([]BatchResult, len(ips))
		for i, ip := range ips {
			out[i] = BatchResult{IP: ip, GeoInfo: nil, Error: ErrClosed}
		}
		return out
	}

	out := make([]BatchResult, len(ips))
	const batchWidth = 4
	n := len(ips)

	for base := 0; base < n; base += batchWidth {
		chunk := n - base
		if chunk > batchWidth {
			chunk = batchWidth
		}

		var (
			v4s      [batchWidth]uint32
			isV4     [batchWidth]bool
			v6s      [batchWidth][16]byte
			isV6     [batchWidth]bool
			rowIDs   [batchWidth]uint32
			errs     [batchWidth]error
			resolved [batchWidth]bool

			// Trie walk state for v4
			active [batchWidth]bool
			idx    [batchWidth]uint32
			suffix [batchWidth]uint32
		)

		for j := 0; j < chunk; j++ {
			ip := ips[base+j]
			if ip == "" {
				errs[j] = newErr(ErrCodeInvalidParam, "invalid ip: ")
				resolved[j] = true
				continue
			}
			res, ok := fastParseIp(ip)
			if !ok {
				errs[j] = newErr(ErrCodeInvalidParam, "invalid ip: "+ip)
				resolved[j] = true
			} else if res.isV4 {
				isV4[j] = true
				v4s[j] = res.v4
			} else {
				isV6[j] = true
				v6s[j] = res.v6
			}
		}

		for j := 0; j < chunk; j++ {
			if isV4[j] {
				if !s.hasV4 || s.offV4Jump <= 0 || s.offV4Jump+uint64(v4s[j]>>16)*4+4 > uint64(len(s.data)) {
					resolved[j] = true
					rowIDs[j] = 0
				} else {
					ptr := binary.LittleEndian.Uint32(s.data[s.offV4Jump+uint64(v4s[j]>>16)*4:])
					if ptr == 0 {
						resolved[j] = true
						rowIDs[j] = 0
					} else if ptr&SENTINEL != 0 {
						resolved[j] = true
						rowIDs[j] = ptr & SENTINEL_MASK_31
					} else {
						idx[j] = ptr & SENTINEL_MASK_31
						suffix[j] = (v4s[j] & 0xFFFF) << 16
						active[j] = true
					}
				}
			}
		}

		mask := s.nodeMask(true)
		for steps := 0; steps < maxTrieWalkSteps; steps++ {
			anyActive := false
			for j := 0; j < chunk; j++ {
				if active[j] {
					child := s.readV4Child(idx[j], (suffix[j]>>31)&1)
					if child == 0 {
						active[j] = false
						resolved[j] = true
						rowIDs[j] = 0
					} else if s.isLeaf(child, true) {
						active[j] = false
						resolved[j] = true
						rowIDs[j] = s.leafValue(child, true)
					} else {
						idx[j] = child & mask
						suffix[j] <<= 1
						anyActive = true
					}
				}
			}
			if !anyActive {
				break
			}
		}

		for j := 0; j < chunk; j++ {
			if active[j] {
				errs[j] = ErrCorrupted
				resolved[j] = true
			}
		}

		for j := 0; j < chunk; j++ {
			if isV6[j] && !resolved[j] {
				rowID, err := s.trieWalkV6(v6s[j])
				rowIDs[j] = rowID
				errs[j] = err
				resolved[j] = true
			}
		}

		for j := 0; j < chunk; j++ {
			ip := ips[base+j]
			if errs[j] != nil {
				out[base+j] = BatchResult{IP: ip, GeoInfo: nil, Error: errs[j]}
			} else if rowIDs[j] == 0 {
				out[base+j] = BatchResult{IP: ip, GeoInfo: nil, Error: nil}
			} else {
				func() {
					defer func() {
						if rec := recover(); rec != nil {
							out[base+j] = BatchResult{IP: ip, GeoInfo: nil, Error: ErrCorrupted}
						}
					}()
					g := s.computeGeoInfoProjected(rowIDs[j], fields)
					out[base+j] = BatchResult{IP: ip, GeoInfo: g, Error: nil}
				}()
			}
		}
	}
	return out
}

// GeoStream 流式惰性查询迭代器（内存恒定，不累积结果）。
type GeoStream struct {
	r      *QzdbReader
	chain  *ChainedReader
	ips    []string
	idx    int
	fields []string
}

// FindStream 返回流式迭代器，逐个惰性求值（ips 为 nil 返回空流）。
func (r *QzdbReader) FindStream(ips []string) *GeoStream {
	if ips == nil {
		return &GeoStream{r: r, ips: nil}
	}
	return &GeoStream{r: r, ips: ips}
}

// FindStreamFields 返回带字段投影的流式迭代器。
func (r *QzdbReader) FindStreamFields(ips []string, fields []string) *GeoStream {
	if ips == nil {
		return &GeoStream{r: r, ips: nil}
	}
	return &GeoStream{r: r, ips: ips, fields: fields}
}

// Next 返回下一条批量结果；当无更多结果时 ok=false。
func (s *GeoStream) Next() (BatchResult, bool) {
	if s == nil || s.idx >= len(s.ips) {
		return BatchResult{}, false
	}
	ip := s.ips[s.idx]
	s.idx++
	var g *GeoInfo
	var err error
	if s.chain != nil {
		if len(s.fields) > 0 {
			g, err = s.chain.FindFields(ip, s.fields)
		} else {
			g, err = s.chain.Find(ip)
		}
	} else if s.r != nil {
		if len(s.fields) > 0 {
			g, err = s.r.FindFields(ip, s.fields)
		} else {
			g, err = s.r.Find(ip)
		}
	}
	return batchEntry(ip, g, err), true
}
