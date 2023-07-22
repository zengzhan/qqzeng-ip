package qqzeng_phone_go

import (
	"encoding/binary"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

type PhoneSearchBest struct {
	data    []byte
	phone2D [][]int
	addrArr []string
	ispArr  []string
}

func NewPhoneSearchBest() *PhoneSearchBest {
	psb := &PhoneSearchBest{}
	psb.LoadDat()
	return psb
}

func (psb *PhoneSearchBest) LoadDat() {
	datPath := filepath.Join(".", "../db/qqzeng-phone-3.0.dat")
	data, err := os.ReadFile(datPath)
	if err != nil {
		fmt.Println("Error reading the dat file:", err)
		os.Exit(1)
	}

	PrefSize := binary.LittleEndian.Uint32(data[0:4])
	descLength := binary.LittleEndian.Uint32(data[8:12])
	ispLength := binary.LittleEndian.Uint32(data[12:16])
	//PhoneSize := binary.LittleEndian.Uint32(data[4:8])
	//verNum := binary.LittleEndian.Uint32(data[16:20])

	headLength := 20
	startIndex := headLength + int(descLength) + int(ispLength)

	// Description array
	descString := string(data[headLength : headLength+int(descLength)])
	addrArr := strings.Split(descString, "&")

	// ISP array
	ispString := string(data[headLength+int(descLength) : headLength+int(descLength)+int(ispLength)])
	ispArr := strings.Split(ispString, "&")

	// Phone2D array
	phone2D := make([][]int, 200)
	for m := 0; m < int(PrefSize); m++ {
		i := m*7 + startIndex
		pref := int(data[i])
		index := int(binary.LittleEndian.Uint32(data[i+1 : i+5]))
		length := int(binary.LittleEndian.Uint16(data[i+5 : i+7]))

		phone2D[pref] = make([]int, 10000)
		for n := 0; n < length; n++ {
			p := startIndex + int(PrefSize)*7 + (n+index)*4
			suff := int(binary.LittleEndian.Uint16(data[p : p+2]))
			addrispIndex := int(binary.LittleEndian.Uint16(data[p+2 : p+4]))
			phone2D[pref][suff] = addrispIndex
		}
	}

	psb.data = data
	psb.addrArr = addrArr
	psb.ispArr = ispArr
	psb.phone2D = phone2D
}

func (psb *PhoneSearchBest) Query(phone string) string {
	prefix := phone[:3]
	suffix := phone[3:7]
	pref, _ := strconv.Atoi(prefix)
	suff, _ := strconv.Atoi(suffix)
	addrispIndex := psb.phone2D[pref][suff]

	if addrispIndex == 0 {
		return ""
	}

	addr := psb.addrArr[addrispIndex/100]
	isp := psb.ispArr[addrispIndex%100]

	return fmt.Sprintf("%s|%s", addr, isp)
}
