package qzdb

import (
	"io"
	"os"
)

// Builder 是 QzdbReader 的构建器（契约 §2）。
//
//	reader, err := qzdb.NewBuilder("ip.qzdb").
//	    GroupIndex(0).
//	    VerifyCRC(true).
//	    Build()
type Builder struct {
	path       string
	buffer     []byte
	reader     io.Reader
	groupIndex int
	verifyCrc  bool
	noCopy     bool // 零拷贝模式：调用方保证 buffer 生命周期内只读不释放
}

// NewBuilder 以文件路径构建。
func NewBuilder(path string) *Builder {
	return &Builder{path: path, verifyCrc: true}
}

// NewBuilderBytes 以内存字节构建（内部拷贝，调用方可自由释放原数组）。
func NewBuilderBytes(b []byte) *Builder {
	cp := make([]byte, len(b))
	copy(cp, b)
	return &Builder{buffer: cp, verifyCrc: true}
}

// NewBuilderBytesNoCopy 以内存字节构建（零拷贝变体）。
// 调用方必须保证 b 在 QzdbReader 生命周期内只读且不被释放，否则行为未定义。
func NewBuilderBytesNoCopy(b []byte) *Builder {
	return &Builder{buffer: b, verifyCrc: true, noCopy: true}
}

// NewBuilderReader 以输入流构建（读取全部字节）。
func NewBuilderReader(rd io.Reader) *Builder {
	return &Builder{reader: rd, verifyCrc: true}
}

// GroupIndex 设置版本组索引（0=主组；ASN 组通常为 2）。
func (b *Builder) GroupIndex(idx int) *Builder {
	b.groupIndex = idx
	return b
}

// VerifyCRC 设置是否开启 CRC32 校验（默认 true；仅 open 可关，reload 强制开启）。
func (b *Builder) VerifyCRC(enabled bool) *Builder {
	b.verifyCrc = enabled
	return b
}

// NoCopy 启用零拷贝模式（仅对字节加载生效）。
// 调用方必须保证 buffer 在 QzdbReader 生命周期内只读且不被释放，否则行为未定义。
func (b *Builder) NoCopy(enabled bool) *Builder {
	b.noCopy = enabled
	return b
}

// Build 构建 QzdbReader。任一异常均 Fail-Closed 拒绝初始化。
func (b *Builder) Build() (*QzdbReader, error) {
	var s *Snapshot
	var err error
	switch {
	case b.reader != nil:
		data, rerr := io.ReadAll(b.reader)
		if rerr != nil {
			return nil, newErr(ErrCodeInvalidParam, "failed to read from reader: "+rerr.Error())
		}
		s, err = buildSnapshotFromBytes(data, b.groupIndex, b.verifyCrc)
	case b.buffer != nil:
		if b.noCopy {
			s, err = buildSnapshot(b.buffer, nil, b.groupIndex, b.verifyCrc)
		} else {
			s, err = buildSnapshotFromBytes(b.buffer, b.groupIndex, b.verifyCrc)
		}
	default:
		if b.path == "" {
			return nil, newErr(ErrCodeInvalidParam, "no database path or bytes provided")
		}
		if _, serr := os.Stat(b.path); serr != nil {
			return nil, newErr(ErrCodeInvalidParam, "database file not accessible: "+b.path)
		}
		s, err = buildSnapshotFromFile(b.path, b.groupIndex, b.verifyCrc)
	}
	if err != nil {
		return nil, err
	}
	r := &QzdbReader{}
	r.installSnapshot(s)
	return r, nil
}
