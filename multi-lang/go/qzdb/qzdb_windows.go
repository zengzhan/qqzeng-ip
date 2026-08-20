//go:build windows

package qzdb

import (
	"os"
	"unsafe"

	"golang.org/x/sys/windows"
)

// mmapFile 在 Windows 上通过 CreateFileMapping + MapViewOfFile 实现只读映射。
// 返回的 release 闭包负责 UnmapViewOfFile + CloseHandle。
func mmapFile(f *os.File, size int) ([]byte, func(), error) {
	// 创建只读文件映射对象；high/low size 传 0 表示映射整个文件。
	h, err := windows.CreateFileMapping(windows.Handle(f.Fd()), nil, windows.PAGE_READONLY, 0, 0, nil)
	if err != nil {
		return nil, nil, err
	}
	// 将文件映射的视图映射到进程地址空间。
	addr, err := windows.MapViewOfFile(h, windows.FILE_MAP_READ, 0, 0, uintptr(size))
	if err != nil {
		_ = windows.CloseHandle(h)
		return nil, nil, err
	}
	// SAFETY: addr 指向由 MapViewOfFile 分配的连续内存区域，长度为 size 字节。
	// 此切片在 release 闭包被调用之前保持有效。
	data := unsafe.Slice((*byte)(unsafe.Pointer(addr)), size)
	release := func() {
		_ = windows.UnmapViewOfFile(addr)
		_ = windows.CloseHandle(h)
	}
	return data, release, nil
}
