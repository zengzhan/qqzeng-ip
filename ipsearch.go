package ipsearch

import (
	"io/ioutil"
	"log"
	"strconv"
	"strings"
)

/**
 * @author xiao.luo
 * @description This is the go version for IpSearch
 */

type ipIndex struct {
	startip, endip             uint32
	local_offset, local_length uint32
}

type prefixIndex struct {
	start_index, end_index uint32
}

type ipSearch struct {
	data               []byte
	prefixMap          map[uint32]prefixIndex
	firstStartIpOffset uint32
	prefixStartOffset  uint32
	prefixEndOffset    uint32
	prefixCount        uint32
}

var ips *ipSearch = nil

func New() (ipSearch, error) {
	if ips == nil {
		var err error
		ips, err = loadIpDat()
		if err != nil {
			log.Fatal("the IP Dat loaded failed!")
			return *ips, err
		}
	}
	return *ips, nil
}

func loadIpDat() (*ipSearch, error) {

	p := ipSearch{}
	//加载ip地址库信息
	data, err := ioutil.ReadFile("F:/ClearClouds/Projects/Java/kepi/src/main/resources/regionIp.dat")
	if err != nil {
		log.Fatal(err)
	}
	p.data = data
	p.prefixMap = make(map[uint32]prefixIndex)

	p.firstStartIpOffset = bytesToLong(data[0], data[1], data[2], data[3])
	p.prefixStartOffset = bytesToLong(data[8], data[9], data[10], data[11])
	p.prefixEndOffset = bytesToLong(data[12], data[13], data[14], data[15])
	p.prefixCount = (p.prefixEndOffset-p.prefixStartOffset)/9 + 1 // 前缀区块每组

	// 初始化前缀对应索引区区间
	indexBuffer := p.data[p.prefixStartOffset:(p.prefixEndOffset + 9)]
	for k := uint32(0); k < p.prefixCount; k++ {
		i := k * 9
		prefix := uint32(indexBuffer[i] & 0xFF)

		pf := prefixIndex{}
		pf.start_index = bytesToLong(indexBuffer[i+1], indexBuffer[i+2], indexBuffer[i+3], indexBuffer[i+4])
		pf.end_index = bytesToLong(indexBuffer[i+5], indexBuffer[i+6], indexBuffer[i+7], indexBuffer[i+8])
		p.prefixMap[prefix] = pf

	}
	return &p, nil
}

func (p ipSearch) Get(ip string) string {
	ips := strings.Split(ip, ".")
	x, _ := strconv.Atoi(ips[0])
	prefix := uint32(x)
	intIP := ipToLong(ip)

	var high uint32 = 0
	var low uint32 = 0

	if _, ok := p.prefixMap[prefix]; ok {
		low = p.prefixMap[prefix].start_index
		high = p.prefixMap[prefix].end_index
	} else {
		return ""
	}

	var my_index uint32
	if low == high {
		my_index = low
	} else {
		my_index = p.binarySearch(low, high, intIP)
	}

	ipindex := ipIndex{}
	ipindex.getIndex(my_index, &p)

	if ipindex.startip <= intIP && ipindex.endip >= intIP {
		return ipindex.getLocal(&p)
	} else {
		return ""
	}
}

// 二分逼近算法
func (p ipSearch) binarySearch(low uint32, high uint32, k uint32) uint32 {
	var M uint32 = 0
	for low <= high {
		mid := (low + high) / 2

		endipNum := p.getEndIp(mid)
		if endipNum >= k {
			M = mid
			if mid == 0 {
				break // 防止溢出
			}
			high = mid - 1
		} else {
			low = mid + 1
		}
	}
	return M
}

// 只获取结束ip的数值
// 索引区第left个索引
// 返回结束ip的数值
func (p ipSearch) getEndIp(left uint32) uint32 {
	left_offset := p.firstStartIpOffset + left*12
	return bytesToLong(p.data[4+left_offset], p.data[5+left_offset], p.data[6+left_offset], p.data[7+left_offset])

}

func (p *ipIndex) getIndex(left uint32, ips *ipSearch) {
	left_offset := ips.firstStartIpOffset + left*12
	p.startip = bytesToLong(ips.data[left_offset], ips.data[1+left_offset], ips.data[2+left_offset], ips.data[3+left_offset])
	p.endip = bytesToLong(ips.data[4+left_offset], ips.data[5+left_offset], ips.data[6+left_offset], ips.data[7+left_offset])
	p.local_offset = bytesToLong3(ips.data[8+left_offset], ips.data[9+left_offset], ips.data[10+left_offset])
	p.local_length = uint32(ips.data[11+left_offset])
}

// / 返回地址信息
// / 地址信息的流位置
// / 地址信息的流长度
func (p *ipIndex) getLocal(ips *ipSearch) string {
	bytes := ips.data[p.local_offset : p.local_offset+p.local_length]
	return string(bytes)

}

func ipToLong(ip string) uint32 {
	quads := strings.Split(ip, ".")
	var result uint32 = 0
	a, _ := strconv.Atoi(quads[3])
	result += uint32(a)
	b, _ := strconv.Atoi(quads[2])
	result += uint32(b) << 8
	c, _ := strconv.Atoi(quads[1])
	result += uint32(c) << 16
	d, _ := strconv.Atoi(quads[0])
	result += uint32(d) << 24
	return result
}

//字节转整形
func bytesToLong(a, b, c, d byte) uint32 {
	a1 := uint32(a)
	b1 := uint32(b)
	c1 := uint32(c)
	d1 := uint32(d)
	return (a1 & 0xFF) | ((b1 << 8) & 0xFF00) | ((c1 << 16) & 0xFF0000) | ((d1 << 24) & 0xFF000000)
}

func bytesToLong3(a, b, c byte) uint32 {
	a1 := uint32(a)
	b1 := uint32(b)
	c1 := uint32(c)
	return (a1 & 0xFF) | ((b1 << 8) & 0xFF00) | ((c1 << 16) & 0xFF0000)

}
