//go:build !windows

package qzdb

import (
	"os"

	"golang.org/x/sys/unix"
)

// mmapFile 只读映射整个文件；返回的 release 闭包负责 munmap。
// 使用 golang.org/x/sys/unix（而非已冻结的标准库 syscall 包）以获得
// 持续维护的跨 Unix 平台（linux/darwin/freebsd/…）覆盖。
func mmapFile(f *os.File, size int) ([]byte, func(), error) {
	data, err := unix.Mmap(int(f.Fd()), 0, size, unix.PROT_READ, unix.MAP_PRIVATE)
	if err != nil {
		return nil, nil, err
	}
	return data, func() { _ = unix.Munmap(data) }, nil
}
