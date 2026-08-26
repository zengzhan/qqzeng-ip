package qzdb

import (
	"net/netip"
	"slices"
	"sync"
)

// ChainMode 定义 ChainedReader 的合并模式（契约 §9.1）。
type ChainMode int

const (
	// ModeFallback 按链顺序依次查找，首个命中即返回。
	ModeFallback ChainMode = iota
	// ModeMerge 字段级合并（先注册者优先）：先注册库的非空值不被覆盖；
	// 先注册库该字段缺失/为空时，才用后面库的值补上。
	ModeMerge
	// ModeMergeOverride 字段级合并（后注册者覆盖）：后注册库的非空值覆盖先注册库。
	ModeMergeOverride
)

// ChainedReader 链式合并多个 reader（契约 §9）。
//
// 三种模式：
//   - ChainFallback: 按添加顺序返回首个命中（默认）
//   - ChainMerge: 字段级合并，先注册者优先
//   - ChainMergeOverride: 字段级合并，后注册者覆盖
//
// ChainedReader 不拥有内部 reader 的生命周期——close() 不会关闭成员库。
// 成员库 reload() 后，ChainedReader 自动读到最新数据（持有引用而非快照）。
type ChainedReader struct {
	mu      sync.RWMutex
	readers []*QzdbReader
	mode    ChainMode
}

// NewChainedReader 创建 Fallback 模式的链式 reader。
func NewChainedReader(readers ...*QzdbReader) *ChainedReader {
	c := &ChainedReader{mode: ModeFallback}
	c.readers = append(c.readers, readers...)
	return c
}

// Chain 创建 Fallback 模式的链式 reader（首个命中即返回）。
func Chain(readers ...*QzdbReader) *ChainedReader {
	return NewChainedReader(readers...)
}

// ChainMerge 创建 Merge 模式链式 reader（字段级合并，先注册者优先）。
func ChainMerge(readers ...*QzdbReader) *ChainedReader {
	c := &ChainedReader{mode: ModeMerge}
	c.readers = append(c.readers, readers...)
	return c
}

// ChainMergeOverride 创建 MergeOverride 模式链式 reader（字段级合并，后注册者覆盖）。
func ChainMergeOverride(readers ...*QzdbReader) *ChainedReader {
	c := &ChainedReader{mode: ModeMergeOverride}
	c.readers = append(c.readers, readers...)
	return c
}

// Add 追加一个 reader 到链尾。
func (c *ChainedReader) Add(r *QzdbReader) {
	c.mu.Lock()
	c.readers = append(c.readers, r)
	c.mu.Unlock()
}

// Readers 返回内部持有的各个 QzdbReader 实例（只读访问）。
func (c *ChainedReader) Readers() []*QzdbReader {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return slices.Clone(c.readers)
}

// Editions 返回每个成员库各自的 edition（按注册顺序）。
func (c *ChainedReader) Editions() []string {
	c.mu.RLock()
	defer c.mu.RUnlock()
	out := make([]string, 0, len(c.readers))
	for _, r := range c.readers {
		if r == nil {
			out = append(out, "")
			continue
		}
		out = append(out, r.GetEdition())
	}
	return out
}

// Scopes 返回每个成员库各自的 scope（按注册顺序）。
func (c *ChainedReader) Scopes() []string {
	c.mu.RLock()
	defer c.mu.RUnlock()
	out := make([]string, 0, len(c.readers))
	for _, r := range c.readers {
		if r == nil {
			out = append(out, "")
			continue
		}
		out = append(out, r.GetScope())
	}
	return out
}

// DataMonths 返回每个成员库各自的数据期号（按注册顺序）。
func (c *ChainedReader) DataMonths() []string {
	c.mu.RLock()
	defer c.mu.RUnlock()
	out := make([]string, 0, len(c.readers))
	for _, r := range c.readers {
		if r == nil {
			out = append(out, "")
			continue
		}
		out = append(out, r.GetDataMonth())
	}
	return out
}

// Find 按链模式查询 IP。
func (c *ChainedReader) Find(ip string) (*GeoInfo, error) {
	c.mu.RLock()
	rs := c.readers
	mode := c.mode
	c.mu.RUnlock()

	if len(rs) == 0 {
		return nil, nil
	}

	if mode == ModeFallback {
		return c.findFallback(ip, rs)
	}
	return c.findMerge(ip, rs, mode)
}

// findFallback 按链顺序返回首个命中的 GeoInfo。
func (c *ChainedReader) findFallback(ip string, rs []*QzdbReader) (*GeoInfo, error) {
	for _, r := range rs {
		if r == nil {
			continue
		}
		g, err := r.Find(ip)
		if err != nil {
			return nil, err
		}
		if g != nil {
			return g, nil
		}
	}
	return nil, nil
}

// findMerge 字段级合并查询（MERGE / MERGE_OVERRIDE）。
// 合并后的 FieldNames 为所有参与库字段名的去重并集：
// 先注册库的字段在前，后注册库独有的新字段依次追加。
func (c *ChainedReader) findMerge(ip string, rs []*QzdbReader, mode ChainMode) (*GeoInfo, error) {
	var fields []string
	fieldVals := make(map[string]string)

	for _, r := range rs {
		if r == nil {
			continue
		}
		g, err := r.Find(ip)
		if err != nil {
			return nil, err
		}
		if g == nil {
			continue
		}
		for i, name := range g.FieldNames {
			val := ""
			if i < len(g.Values) {
				val = g.Values[i]
			}
			if _, exists := fieldVals[name]; !exists {
				fields = append(fields, name)
				fieldVals[name] = val
				continue
			}
			if mode == ModeMerge {
				if fieldVals[name] == "" {
					fieldVals[name] = val
				}
			} else {
				if val != "" {
					fieldVals[name] = val
				}
			}
		}
	}

	if len(fields) == 0 {
		return nil, nil
	}

	values := make([]string, len(fields))
	for i, f := range fields {
		values[i] = fieldVals[f]
	}
	return &GeoInfo{
		FieldNames: fields,
		Values:     values,
		normMap:    buildNormalizedMap(fields),
	}, nil
}

// FindStr 返回首个命中/合并后的 to_pipe 字符串；全未命中返回 ""。
func (c *ChainedReader) FindStr(ip string) string {
	g, _ := c.Find(ip)
	if g == nil {
		return ""
	}
	return g.ToPipe()
}

// FindAddr 查询 netip.Addr（Go 原生 IP 类型重载）。
func (c *ChainedReader) FindAddr(addr netip.Addr) (*GeoInfo, error) {
	if !addr.IsValid() {
		return nil, nil
	}
	if addr.Is4() {
		a4 := addr.As4()
		return c.FindUint(uint32(a4[0])<<24 | uint32(a4[1])<<16 | uint32(a4[2])<<8 | uint32(a4[3]))
	}
	if addr.Is4In6() {
		a4 := addr.As4()
		return c.FindUint(uint32(a4[0])<<24 | uint32(a4[1])<<16 | uint32(a4[2])<<8 | uint32(a4[3]))
	}
	ip16 := addr.As16()
	return c.FindBytes(ip16)
}

// FindUint 查询 IPv4 uint32（契约 §5.2）。
func (c *ChainedReader) FindUint(ipInt uint32) (*GeoInfo, error) {
	c.mu.RLock()
	rs := c.readers
	mode := c.mode
	c.mu.RUnlock()

	if len(rs) == 0 {
		return nil, nil
	}
	if mode == ModeFallback {
		for _, r := range rs {
			if r == nil {
				continue
			}
			g, err := r.FindUint(ipInt)
			if err != nil {
				return nil, err
			}
			if g != nil {
				return g, nil
			}
		}
		return nil, nil
	}
	addr := netip.AddrFrom4([4]byte{byte(ipInt >> 24), byte(ipInt >> 16), byte(ipInt >> 8), byte(ipInt)})
	return c.findMerge(addr.String(), rs, mode)
}

// FindBytes 查询 16 字节地址（IPv4-mapped 自动降级）。
func (c *ChainedReader) FindBytes(ip16 [16]byte) (*GeoInfo, error) {
	c.mu.RLock()
	rs := c.readers
	mode := c.mode
	c.mu.RUnlock()

	if len(rs) == 0 {
		return nil, nil
	}
	if mode == ModeFallback {
		for _, r := range rs {
			if r == nil {
				continue
			}
			g, err := r.FindBytes(ip16)
			if err != nil {
				return nil, err
			}
			if g != nil {
				return g, nil
			}
		}
		return nil, nil
	}
	if isV4Mapped(ip16) {
		return c.FindUint(v4FromMapped(ip16))
	}
	addr := netip.AddrFrom16(ip16)
	if !addr.IsValid() {
		return nil, nil
	}
	return c.findMerge(addr.String(), rs, mode)
}

// FindFields 字段投影查询：先执行合并查询再投影。
func (c *ChainedReader) FindFields(ip string, fields []string) (*GeoInfo, error) {
	full, err := c.Find(ip)
	if err != nil {
		return nil, err
	}
	if full == nil || len(fields) == 0 {
		return full, nil
	}
	return projectGeo(full, fields), nil
}

// FindBatch 顺序批量查询（保留三态语义，内部不起线程池）。
// 通过 batchEntry 收敛：非法 IP 标记 Error=INVALID_PARAM，与 QzdbReader.FindBatch 一致。
func (c *ChainedReader) FindBatch(ips []string) []BatchResult {
	if ips == nil {
		return nil
	}
	out := make([]BatchResult, 0, len(ips))
	for _, ip := range ips {
		g, err := c.Find(ip)
		out = append(out, batchEntry(ip, g, err))
	}
	return out
}

// FindBatchFields 顺序批量字段投影查询。
func (c *ChainedReader) FindBatchFields(ips []string, fields []string) []BatchResult {
	if ips == nil {
		return nil
	}
	out := make([]BatchResult, 0, len(ips))
	for _, ip := range ips {
		g, err := c.FindFields(ip, fields)
		out = append(out, batchEntry(ip, g, err))
	}
	return out
}

// FindStream 返回流式迭代器，逐个惰性求值。
func (c *ChainedReader) FindStream(ips []string) *GeoStream {
	if ips == nil {
		return &GeoStream{chain: c, ips: nil}
	}
	return &GeoStream{chain: c, ips: ips}
}

// QzdbRegistry 管理多个命名 QzdbReader。
type QzdbRegistry struct {
	mu      sync.RWMutex
	names   []string
	readers map[string]*QzdbReader
	order   []*QzdbReader

	// quarantine 保存最近被替换/注销的 reader，延迟关闭而不是立即 Close()。
	//
	// 为什么需要它：Get(name) 把 *QzdbReader 本体交给调用方；调用方完全可能
	// 先 Get() 拿到引用，还没来得及 Find()，另一个 goroutine 的热更新
	// Register(name, newReader) 就把旧 reader 换掉了。QzdbReader.Close() 本身
	// 对并发调用是安全的（不会 panic，只会让后续 Find() 返回 ErrClosed，
	// 见 Close()/snapshot() 的实现），但对调用方来说仍然是一次不该发生的
	// "毫无预兆的查询失败"。Register/Unregister 是运维触发的低频动作
	// （分钟级以上间隔），而一次 Find() 只是微秒级临界区，所以把刚替换下来的
	// reader 在有限容量的队列里多留几轮，能让几乎所有在途调用安全完成，
	// 同时把最坏情况（进程一直不退出、reader 永不释放）限制在 quarantineCap
	// 个以内，而不是无限增长。
	quarantine []*QzdbReader
}

const registryQuarantineCap = 8

// NewQzdbRegistry 创建空注册表。
func NewQzdbRegistry() *QzdbRegistry {
	return &QzdbRegistry{readers: make(map[string]*QzdbReader)}
}

// Register 注册一个命名 reader（注册顺序决定 Find() 的查找优先级）。
// 对已存在的 name 重复调用会原地替换 order 中的旧条目（热更新对 Find() 立即生效），
// 而不是把旧 reader 悄悄留在 order 里继续被使用；旧 reader 移入退休队列延迟关闭。
func (reg *QzdbRegistry) Register(name string, r *QzdbReader) {
	reg.mu.Lock()
	defer reg.mu.Unlock()
	if old, exists := reg.readers[name]; exists {
		if i := slices.Index(reg.order, old); i >= 0 {
			reg.order[i] = r
		}
		reg.retireLocked(old)
	} else {
		reg.names = append(reg.names, name)
		reg.order = append(reg.order, r)
	}
	reg.readers[name] = r
}

// Unregister 移除一个命名 reader；旧 reader 移入退休队列延迟关闭。
// 未注册的 name 是安全的空操作。
func (reg *QzdbRegistry) Unregister(name string) {
	reg.mu.Lock()
	defer reg.mu.Unlock()
	old, exists := reg.readers[name]
	if !exists {
		return
	}
	delete(reg.readers, name)
	if i := slices.Index(reg.names, name); i >= 0 {
		reg.names = slices.Delete(reg.names, i, i+1)
	}
	if i := slices.Index(reg.order, old); i >= 0 {
		reg.order = slices.Delete(reg.order, i, i+1)
	}
	reg.retireLocked(old)
}

// retireLocked 把 old 放入退休队列；超出容量时关闭最早退休的 reader。
// 调用方必须已持有 reg.mu 的写锁。
func (reg *QzdbRegistry) retireLocked(old *QzdbReader) {
	if old == nil {
		return
	}
	reg.quarantine = append(reg.quarantine, old)
	for len(reg.quarantine) > registryQuarantineCap {
		evicted := reg.quarantine[0]
		reg.quarantine = reg.quarantine[1:]
		_ = evicted.Close()
	}
}

// Clear 移除并关闭全部已注册的 reader（视为终止关闭动作，非热更新，
// 因此同步立即 Close，语义与直接调用某个 reader 的 Close() 一致）。
// 退休队列中尚未关闭的旧 reader 一并冲刷关闭。
func (reg *QzdbRegistry) Clear() {
	reg.mu.Lock()
	order := reg.order
	quarantined := reg.quarantine
	reg.names = nil
	reg.order = nil
	reg.quarantine = nil
	reg.readers = make(map[string]*QzdbReader)
	reg.mu.Unlock()

	for _, r := range order {
		if r != nil {
			_ = r.Close()
		}
	}
	for _, r := range quarantined {
		if r != nil {
			_ = r.Close()
		}
	}
}

// Get 按名称取回 reader。
func (reg *QzdbRegistry) Get(name string) *QzdbReader {
	reg.mu.RLock()
	defer reg.mu.RUnlock()
	return reg.readers[name]
}

// Names 返回注册名称列表（注册顺序）。
func (reg *QzdbRegistry) Names() []string {
	reg.mu.RLock()
	defer reg.mu.RUnlock()
	return slices.Clone(reg.names)
}

// Find 按注册顺序查询，返回首个非空的 GeoInfo；全未命中返回 (nil, nil)。
func (reg *QzdbRegistry) Find(ip string) (*GeoInfo, error) {
	reg.mu.RLock()
	order := reg.order
	reg.mu.RUnlock()
	for _, r := range order {
		if r == nil {
			continue
		}
		g, err := r.Find(ip)
		if err != nil {
			return nil, err
		}
		if g != nil {
			return g, nil
		}
	}
	return nil, nil
}

// FindStr 返回首个命中的 to_pipe 字符串；全未命中返回 ""。
func (reg *QzdbRegistry) FindStr(ip string) string {
	g, _ := reg.Find(ip)
	if g == nil {
		return ""
	}
	return g.ToPipe()
}
