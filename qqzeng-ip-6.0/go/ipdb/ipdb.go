package ipdb

import (
	"encoding/binary"
	"errors"
	"io/ioutil"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

// IpDbSearch 结构体
type IpDbSearch struct {
	data      []byte
	geoispArr []string
}

// 单例实例
var (
	instance *IpDbSearch
	once     sync.Once
	initErr  error
)

const (
	IndexStartIndex = 0x30004
	EndMask         = 0x800000
	ComplMask       = ^EndMask
	DbFileName      = "qqzeng-ip-6.0-global.db"
)

// Instance 获取单例
func Instance() (*IpDbSearch, error) {
	once.Do(func() {
		instance = &IpDbSearch{}
		initErr = instance.loadDb()
	})
	return instance, initErr
}

// loadDb 加载数据库
func (searcher *IpDbSearch) loadDb() error {
	dbPath := findDbPath()
	if dbPath == "" {
		return errors.New("fatal: cannot find " + DbFileName)
	}

	var err error
	searcher.data, err = ioutil.ReadFile(dbPath)
	if err != nil {
		return err
	}

	if len(searcher.data) < IndexStartIndex {
		return errors.New("invalid database file")
	}

	// 节点数量 (小端序)
	nodeCount := int(binary.LittleEndian.Uint32(searcher.data[:4]))
	stringAreaOffset := IndexStartIndex + nodeCount*6

	if stringAreaOffset > len(searcher.data) {
		return errors.New("invalid metadata")
	}

	// 解析字符串区
	content := string(searcher.data[stringAreaOffset:])
	searcher.geoispArr = strings.Split(content, "\t")

	return nil
}

// Find 查询IP (String -> String)
func (searcher *IpDbSearch) Find(ip string) string {
	if ip == "" {
		return ""
	}
	prefix, suffix, err := fastParseIp(ip)
	if err != nil {
		return ""
	}
	return searcher.FindUint(prefix, suffix)
}

// FindUint 查询IP (Uint -> String)
func (searcher *IpDbSearch) FindUint(prefix, suffix uint16) string {
	// 一级索引
	record := searcher.readInt24(4 + int(prefix)*3)

	// 二叉树遍历
	for (record & EndMask) != EndMask {
		bit := (suffix >> 15) & 1
		offset := IndexStartIndex + record*6 + int(bit)*3
		record = searcher.readInt24(offset)
		suffix <<= 1
	}

	index := record & ComplMask
	if index < len(searcher.geoispArr) {
		return searcher.geoispArr[index]
	}
	return ""
}

// readInt24 读取3字节整数 (大端序逻辑)
func (searcher *IpDbSearch) readInt24(offset int) int {
	return int(searcher.data[offset])<<16 | int(searcher.data[offset+1])<<8 | int(searcher.data[offset+2])
}

// fastParseIp 高性能IP解析
func fastParseIp(ip string) (uint16, uint16, error) {
	var val uint
	var result uint
	shift := 24

	for i := 0; i < len(ip); i++ {
		c := ip[i]
		if c >= '0' && c <= '9' {
			val = val*10 + uint(c-'0')
		} else if c == '.' {
			if val > 255 {
				return 0, 0, errors.New("invalid ip")
			}
			result |= val << shift
			val = 0
			shift -= 8
		} else {
			return 0, 0, errors.New("invalid char")
		}
	}

	if val > 255 || shift != 0 {
		return 0, 0, errors.New("invalid ip")
	}
	result |= val

	return uint16(result >> 16), uint16(result & 0xFFFF), nil
}

// findDbPath 查找数据库路径
func findDbPath() string {
	// 获取当前执行文件路径
	exePath, err := os.Executable()
	if err != nil {
		exePath = "."
	}
	baseDir := filepath.Dir(exePath)

	attempts := []string{
		filepath.Join(baseDir, DbFileName),
		filepath.Join(baseDir, "../data", DbFileName),       // 上级data目录
		filepath.Join(baseDir, "../../data", DbFileName),    // 上上级
		filepath.Join(baseDir, "../../../data", DbFileName), // 上上上级
		"../data/" + DbFileName,                             // 相对路径
	}

	for _, path := range attempts {
		if _, err := os.Stat(path); err == nil {
			return path
		}
	}
	return ""
}
