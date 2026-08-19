package qqzengip


import (	
	
	"io/ioutil"
	"log"
	"strconv"
	"strings"
)


type IpSearch struct {	
	prefStart   [256]uint32
	prefEnd     [256]uint32
	endArr      []uint32
	addrArr     []string

}

var ips *IpSearch = nil


func Location() (IpSearch, error) {
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

func loadIpDat() (*IpSearch, error) {

	p := IpSearch{}
	data, err := ioutil.ReadFile("./qqzeng-ip-3.0-ultimate.dat")
	if err != nil {
		log.Fatal(err)
	}
	
	for  k := 0; k < 256; k++ {
		i:= k * 8 + 4			
		p.prefStart[k] =  ReadLittleEndian32(data[i], data[i + 1], data[i + 2], data[i + 3])
		p.prefEnd[k] =  ReadLittleEndian32(data[i + 4], data[i + 5], data[i + 6], data[i + 7])
	}

	RecordSize:=int(ReadLittleEndian32(data[0], data[1], data[2], data[3]))

	p.endArr= make([]uint32, RecordSize)
	p.addrArr=make([]string, RecordSize)
	for  i := 0; i < RecordSize; i++ {
		 j := 2052 + (i * 8)
		 endipnum := ReadLittleEndian32(data[j], data[1 +j], data[2 +j], data[3 + j])
		 offset:= ReadLittleEndian24(data[4 + j],data[5 +j],data[6 +j])
		 length:=uint32(data[7 +j])
		 p.endArr[i] = endipnum
		 p.addrArr[i] =string( data[offset :int(offset+length)])
	}

	return &p, nil
}

func (p IpSearch) Get(ip string) string {
	ips := strings.Split(ip, ".")
	x, _ := strconv.Atoi(ips[0])
	prefix := uint32(x)
	intIP := ipToLong(ip)

	low := p.prefStart[prefix]
	high:=p.prefEnd[prefix]
	var cur uint32	
	if (low ==high ) { cur=low } else {	cur=p.binarySearch(low, high, intIP) } 
	return p.addrArr[cur];

}

// 二分逼近算法
func (p IpSearch) binarySearch(low uint32, high uint32, k uint32) uint32 {
	var M uint32 = 0
	for low <= high {
		mid := (low + high) / 2

		endipNum := p.endArr[mid]
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
func ReadLittleEndian32(a, b, c, d byte) uint32 {
	a1 := uint32(a)
	b1 := uint32(b)
	c1 := uint32(c)
	d1 := uint32(d)
	return (a1 & 0xFF) | ((b1 << 8) & 0xFF00) | ((c1 << 16) & 0xFF0000) | ((d1 << 24) & 0xFF000000)
}

func ReadLittleEndian24(a, b, c byte) uint32 {
	a1 := uint32(a)
	b1 := uint32(b)
	c1 := uint32(c)
	return (a1 & 0xFF) | ((b1 << 8) & 0xFF00) | ((c1 << 16) & 0xFF0000)

}
