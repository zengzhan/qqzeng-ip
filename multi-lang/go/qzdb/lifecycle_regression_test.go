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
