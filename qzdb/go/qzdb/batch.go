package qzdb

// BatchResult 批量查询的单条结果（保留三态语义）。
type BatchResult struct {
	IP      string
	GeoInfo *GeoInfo
	Error   error
}

// FindBatch 顺序批量查询（内部不起线程池）；逐条保留三态语义。ips 为 nil 返回空列表。
func (r *QzdbReader) FindBatch(ips []string) []BatchResult {
	if ips == nil {
		return nil
	}
	out := make([]BatchResult, 0, len(ips))
	for _, ip := range ips {
		g, err := r.Find(ip)
		out = append(out, BatchResult{IP: ip, GeoInfo: g, Error: err})
	}
	return out
}

// FindBatchFields 顺序批量字段投影查询。
func (r *QzdbReader) FindBatchFields(ips []string, fields []string) []BatchResult {
	if ips == nil {
		return nil
	}
	out := make([]BatchResult, 0, len(ips))
	for _, ip := range ips {
		g, err := r.FindFields(ip, fields)
		out = append(out, BatchResult{IP: ip, GeoInfo: g, Error: err})
	}
	return out
}

// GeoStream 流式惰性查询迭代器（内存恒定，不累积结果）。
type GeoStream struct {
	r       *QzdbReader
	chain   *ChainedReader
	ips     []string
	idx     int
	fields  []string
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
	return BatchResult{IP: ip, GeoInfo: g, Error: err}, true
}
