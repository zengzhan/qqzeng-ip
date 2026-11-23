//go:build js || wasip1 || plan9

package qzdb

import (
	"io"
	"os"
)

// mmapFile 在不支持 POSIX mmap 的平台（js/wasm、wasip1/wasm、plan9）上
// 回退为整文件读入堆内存，保证跨平台可编译可运行。
func mmapFile(f *os.File, size int) ([]byte, func(), error) {
	data := make([]byte, size)
	if _, err := io.ReadFull(f, data); err != nil {
		return nil, nil, err
	}
	return data, func() {}, nil
}
