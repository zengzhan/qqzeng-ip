package qzdb

import (
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

// 回归测试（finalizer 生命周期模型）：快照生命周期由 GC 可达性托管——
// 读者持有引用 ⇒ finalizer（munmap）必不运行。Close 之后仍被本地引用持有的
// 快照必须可以继续安全查询（UAF 会在此崩溃，-race 下更灵敏）。
func TestSnapshotUsableAfterCloseWhileHeld(t *testing.T) {
	data := buildSyntheticDB(t)
	path := filepath.Join(t.TempDir(), "syn.qzdb")
	if err := os.WriteFile(path, data, 0o644); err != nil {
		t.Fatal(err)
	}
	r, err := Open(path, 0, false)
	if err != nil {
		t.Fatal(err)
	}

	s := r.snapshot()
	if s == nil {
		t.Fatal("snapshot 不应为 nil")
	}
	before := s.lookupV4PrefixLen(0x72720101)

	if err := r.Close(); err != nil {
		t.Fatal(err)
	}
	// 强制两轮 GC（finalizer 需两轮才可能运行），再经持有的引用查询
	runtime.GC()
	runtime.GC()
	after := s.lookupV4PrefixLen(0x72720101)
	if before != after {
		t.Fatalf("Close+GC 后持有引用的快照结果改变: before=%d after=%d", before, after)
	}
	if _, err := r.Find("114.114.1.1"); err != nil && err != ErrClosed {
		t.Fatalf("关闭后查询应安全失败（nil/ErrClosed），got err=%v", err)
	}
	if err := r.Close(); err != nil { // 二次 Close 幂等
		t.Fatal(err)
	}
	runtime.KeepAlive(s)
}

// 回归测试（P1：Close/Reload 复活竞态）：旧实现 Close 只做 Swap(nil)，Reload 在
// 构建完成后无条件发布快照——「Reload 开始 → Close 完成 → Reload 发布」交错会
// 把 reader 复活。现实现以 closedSnap 哨兵 + CAS 发布：关闭后发布必然失败。
// 用 reloadPublishGate 屏障确定性制造该交错（非随机碰运气）。
func TestReloadAfterCloseDoesNotResurrect(t *testing.T) {
	data := buildSyntheticDB(t)
	path := filepath.Join(t.TempDir(), "syn.qzdb")
	if err := os.WriteFile(path, data, 0o644); err != nil {
		t.Fatal(err)
	}
	r, err := Open(path, 0, false) // mmap 路径：发布失败须显式 munmap
	if err != nil {
		t.Fatal(err)
	}

	gate := make(chan struct{})
	wait := make(chan struct{})
	old := reloadPublishGate
	reloadPublishGate = func() { close(gate); <-wait }
	defer func() { reloadPublishGate = old }()

	errCh := make(chan error, 1)
	go func() { errCh <- r.Reload(path) }()
	<-gate // Reload 已构建完成，停在发布前
	if err := r.Close(); err != nil {
		t.Fatal(err)
	}
	close(wait) // 放行发布

	if err := <-errCh; err == nil {
		t.Fatal("Close 完成后的 Reload 必须失败，不得复活 reader")
	}
	if s := r.snapshot(); s != nil {
		t.Fatal("Close 后快照槽位必须保持关闭态（不可复活）")
	}
	if _, err := r.Find("114.114.1.1"); err != ErrClosed {
		t.Fatalf("Close 后查询应返回 ErrClosed, got %v", err)
	}
	if err := r.Reload(path); err == nil {
		t.Fatal("关闭后再次 Reload 仍须拒绝")
	}
	if err := r.Close(); err != nil { // 二次 Close 幂等
		t.Fatal(err)
	}
}

// 回归测试：Close 与并发 Reload 的锤击（-race 下运行）。不变式：Close 一旦返回，
// 无论有多少在途 Reload，最终槽位必须保持关闭态。
func TestCloseVsReloadHammer(t *testing.T) {
	data := buildSyntheticDB(t)
	path := filepath.Join(t.TempDir(), "syn.qzdb")
	if err := os.WriteFile(path, data, 0o644); err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 32; i++ {
		r, err := Open(path, 0, false)
		if err != nil {
			t.Fatal(err)
		}
		done := make(chan struct{})
		go func() {
			defer close(done)
			for j := 0; j < 8; j++ {
				_ = r.Reload(path)
				_ = r.ReloadBuffer(data)
			}
		}()
		_ = r.Close()
		<-done
		if s := r.snapshot(); s != nil {
			t.Fatalf("第 %d 轮：Close 与 Reload 并发后 reader 被复活", i)
		}
	}
}

// 回归测试：Close 与并发查询的竞态。旧实现的 snapshot() 在 Load 与 Add(1) 之间
// 存在窗口，Close 可在该窗口内 munmap，导致读者对已释放内存做原子写。
// 用 -race 运行可捕获旧实现的竞态。
func TestCloseVsConcurrentQueriesRace(t *testing.T) {
	data := buildSyntheticDB(t)
	path := filepath.Join(t.TempDir(), "syn.qzdb")
	if err := os.WriteFile(path, data, 0o644); err != nil {
		t.Fatal(err)
	}

	const rounds = 64
	for i := 0; i < rounds; i++ {
		r, err := Open(path, 0, false)
		if err != nil {
			t.Fatal(err)
		}
		done := make(chan struct{})
		go func() {
			defer close(done)
			for j := 0; j < 200; j++ {
				_, _ = r.Find("114.114.1.1")
				_ = r.LookupCidr("114.114.1.1")
			}
		}()
		// 与查询 goroutine 并发 Close（本轮可能 Close 在前，查询安全失败；
		// 也可能 Close 在后——旧实现此时存在 UAF 窗口）
		if err := r.Close(); err != nil {
			t.Fatal(err)
		}
		<-done
	}
}

// 回归测试：批量路径保留「非法 IP vs 未命中」三态（契约 §4）。
// 修复前 Go 的 BatchResult.Error 恒为 nil，两者不可区分。
func TestBatchTriStateInvalidVsMiss(t *testing.T) {
	data := buildSyntheticDB(t)
	path := filepath.Join(t.TempDir(), "syn.qzdb")
	if err := os.WriteFile(path, data, 0o644); err != nil {
		t.Fatal(err)
	}
	r, err := Open(path, 0, false)
	if err != nil {
		t.Fatal(err)
	}
	defer r.Close()

	batch := r.FindBatch([]string{"114.114.1.1", "not-an-ip", "1.2.3.4.5"})
	if len(batch) != 3 {
		t.Fatalf("len=%d want 3", len(batch))
	}
	// 合法且覆盖 → geo 非 nil、无 error
	if batch[0].GeoInfo == nil || batch[0].Error != nil {
		t.Fatalf("hit: geo=%v err=%v", batch[0].GeoInfo, batch[0].Error)
	}
	// 非法 IP → geo nil + error 非 nil（修复前 error 恒 nil）
	if batch[1].GeoInfo != nil || batch[1].Error == nil {
		t.Fatalf("invalid: geo=%v err=%v, want (nil, non-nil)", batch[1].GeoInfo, batch[1].Error)
	}
	if batch[2].GeoInfo != nil || batch[2].Error == nil {
		t.Fatalf("invalid#2: geo=%v err=%v, want (nil, non-nil)", batch[2].GeoInfo, batch[2].Error)
	}

	// 字段投影批量的同款三态
	bf := r.FindBatchFields([]string{"114.114.1.1", "not-an-ip"}, []string{"isp"})
	if bf[0].GeoInfo == nil || bf[0].Error != nil {
		t.Fatalf("fields hit: geo=%v err=%v", bf[0].GeoInfo, bf[0].Error)
	}
	if bf[1].GeoInfo != nil || bf[1].Error == nil {
		t.Fatalf("fields invalid: geo=%v err=%v, want (nil, non-nil)", bf[1].GeoInfo, bf[1].Error)
	}

	// 流式路径同款
	st := r.FindStream([]string{"not-an-ip"})
	res, ok := st.Next()
	if !ok || res.GeoInfo != nil || res.Error == nil {
		t.Fatalf("stream invalid: ok=%v geo=%v err=%v", ok, res.GeoInfo, res.Error)
	}
}

// 回归测试：publishSnapshot 被 closedSnap 拒绝时，releaseUnpublished 必须
// 立即显式调用 release() 归还 mmap，而不是等待 GC finalizer。
func TestReleaseUnpublishedOnRejectedPublish(t *testing.T) {
	data := buildSyntheticDB(t)
	r, err := OpenBufferNoCopy(data, 0, false)
	if err != nil {
		t.Fatal(err)
	}
	if err := r.Close(); err != nil {
		t.Fatal(err)
	}

	released := false
	ns, err := buildSnapshot(data, func() { released = true }, 0, false)
	if err != nil {
		t.Fatal(err)
	}
	if r.publishSnapshot(ns) {
		t.Fatal("已关闭的 reader 不得接受新快照发布")
	}
	if released {
		t.Fatal("publishSnapshot 失败前不应提前释放")
	}
	releaseUnpublished(ns)
	if !released {
		t.Fatal("releaseUnpublished 必须立即调用 release() 释放未发布映射")
	}
	if s := r.snapshot(); s != nil {
		t.Fatal("关闭态不可被未发布快照覆盖")
	}
}
