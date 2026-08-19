package main

import (
	"encoding/binary"
	"errors"
	"fmt"
	"math/bits"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

const (
	headerSize           = 32    // 8个uint的头部
	prefixCount          = 200   // 电话号码前缀总数（0-199）
	bitmapPopCountOffset = 0x4E2 // 位图统计信息偏移量
)

type PhoneSearch6Db struct {
	data       []byte                // 数据库原始数据
	regionIsps []string              // 地区-运营商组合缓存
	index      [prefixCount][2]int32 // 前缀索引表
}

var (
	instance *PhoneSearch6Db
	once     sync.Once
)

// 单例模式获取实例
func GetInstance() *PhoneSearch6Db {
	once.Do(func() {
		instance = &PhoneSearch6Db{}
		err := instance.LoadDatabase()
		if err != nil {
			panic(fmt.Sprintf("Failed to load database: %v", err))
		}
	})
	return instance
}

// 加载并解析数据库文件
func (db *PhoneSearch6Db) LoadDatabase() error {
	filePath := filepath.Join(".", "qqzeng-phone-6.0.db")
	data, err := os.ReadFile(filePath)
	if err != nil {
		return fmt.Errorf("failed to read file: %w", err)
	}
	db.data = data

	// 解析头部（小端序）
	header := make([]uint32, 8)
	for i := 0; i < len(header); i++ {
		header[i] = binary.LittleEndian.Uint32(db.data[i*4 : (i+1)*4])
	}

	// 计算偏移量
	regionsStart := headerSize
	ispsStart := regionsStart + int(header[1])
	indexStart := ispsStart + int(header[2])

	// 解析地区和运营商表
	regions := strings.Split(string(db.data[regionsStart:ispsStart]), "&")
	isps := strings.Split(string(db.data[ispsStart:indexStart]), "&")

	// 构建地区-运营商组合
	db.regionIsps = make([]string, header[4])
	entryOffset := int(header[3])
	for i := 0; i < len(db.regionIsps); i++ {
		entry := binary.LittleEndian.Uint16(db.data[entryOffset+i*2 : entryOffset+i*2+2])
		db.regionIsps[i] = fmt.Sprintf("%s|%s", regions[entry>>5], isps[entry&0x1F])
	}

	// 构建前缀索引表
	pos := indexStart
	for i := 0; i < prefixCount; i++ {
		prefix := binary.LittleEndian.Uint32(db.data[pos : pos+4])
		if prefix == uint32(i) {
			db.index[i][0] = int32(binary.LittleEndian.Uint32(db.data[pos+4 : pos+8]))
			db.index[i][1] = int32(binary.LittleEndian.Uint32(db.data[pos+8 : pos+12]))
			pos += 12
		} else {
			db.index[i][0] = 0
			db.index[i][1] = 0
		}
	}

	return nil
}

// 查询电话号码归属地信息
func (db *PhoneSearch6Db) Query(phone string) (string, error) {
	if len(phone) != 7 {
		return "", errors.New("invalid phone number length")
	}

	// 解析前缀和后四位
	prefix, err := parsePhoneSegment(phone[:3])
	if err != nil {
		return "", err
	}
	subNum, err := parsePhoneSegment(phone[3:])
	if err != nil {
		return "", err
	}

	if prefix >= prefixCount {
		return "", nil
	}

	// 获取索引条目
	bitmapOffset, dataOffset := db.index[prefix][0], db.index[prefix][1]
	if bitmapOffset == 0 || dataOffset == 0 {
		return "", nil
	}

	// 位图检查
	byteIndex := subNum >> 3
	bitIndex := subNum & 0b0111
	if int(bitmapOffset)+byteIndex >= len(db.data) {
		return "", nil
	}

	bitmap := db.data[bitmapOffset+int32(byteIndex)]
	if (bitmap & (1 << bitIndex)) == 0 {
		return "", nil
	}

	// 计算有效数据位置
	popCountOffset := int(bitmapOffset) + bitmapPopCountOffset + (byteIndex << 1)
	preCount := binary.LittleEndian.Uint16(db.data[popCountOffset : popCountOffset+2])
	localCount := bits.OnesCount8(bitmap & ((1 << bitIndex) - 1))

	// 定位最终数据
	dataPos := int(dataOffset) + ((int(preCount) + localCount) << 1)
	entry := binary.LittleEndian.Uint16(db.data[dataPos : dataPos+2])
	if int(entry) < len(db.regionIsps) {
		return db.regionIsps[entry], nil
	}

	return "", nil
}

// 将电话号码段转换为数字
func parsePhoneSegment(segment string) (int, error) {
	result := 0
	for _, c := range segment {
		if c < '0' || c > '9' {
			return 0, errors.New("invalid phone segment")
		}
		result = result*10 + int(c-'0')
	}
	return result, nil
}

// 测试主函数  运行 go run PhoneSearch6Db.go
func main() {
	db := GetInstance()

	// 测试查询有效号码
	if result, err := db.Query("1933092"); err != nil {
		fmt.Println("Error:", err)
	} else if result != "" {
		fmt.Printf("Query Result for '1234567': %s\n", result)
	} else {
		fmt.Println("No result found for '1234567'")
	}

	// 测试查询无效号码
	if result, err := db.Query("9999999"); err != nil {
		fmt.Println("Error:", err)
	} else if result != "" {
		fmt.Printf("Query Result for '9999999': %s\n", result)
	} else {
		fmt.Println("No result found for '9999999'")
	}
}
