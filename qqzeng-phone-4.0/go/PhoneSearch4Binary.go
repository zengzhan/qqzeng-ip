package main

import (
	"encoding/binary"
	"fmt"
	"io/ioutil"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

//手机号码归属地查询 go 解析 4.0 版
// go1.22.0

// PhoneSearch4Binary struct
type PhoneSearch4Binary struct {
	prefDict map[string]int
	data     []byte
	addrArr  []string
	ispArr   []string
}

// LoadDat loads the binary dat file
func (p *PhoneSearch4Binary) LoadDat() error {
	datPath := filepath.Join(".", "qqzeng-phone-4.0.dat")

	data, err := ioutil.ReadFile(datPath)
	if err != nil {
		return err
	}

	p.data = data

	PrefSize := binary.LittleEndian.Uint32(p.data[0:4])
	descLength := binary.LittleEndian.Uint32(p.data[8:12])
	ispLength := binary.LittleEndian.Uint32(p.data[12:16])

	headLength := 20
	startIndex := headLength + int(descLength) + int(ispLength)

	descString := string(p.data[headLength : headLength+int(descLength)])
	p.addrArr = strings.Split(descString, "&")

	ispString := string(p.data[headLength+int(descLength) : headLength+int(descLength)+int(ispLength)])
	p.ispArr = strings.Split(ispString, "&")

	p.prefDict = make(map[string]int)

	for m := 0; m < int(PrefSize); m++ {
		i := m*5 + startIndex
		pref := strconv.Itoa(int(p.data[i]))
		index := int(binary.LittleEndian.Uint32(p.data[i+1 : i+5]))
		p.prefDict[pref] = index
	}

	return nil
}

// Query searches the phone number
func (p *PhoneSearch4Binary) Query(phone string) string {

	prefix := phone[0:3]
	suffix, err := strconv.Atoi(phone[3:7])
	if err != nil {
		return ""
	}

	start, ok := p.prefDict[prefix]
	if !ok {
		return ""
	}

	pIndex := start + suffix<<1
	addrispIndex := int(binary.LittleEndian.Uint16(p.data[pIndex : pIndex+2]))

	if addrispIndex == 0 {
		return "|||||"
	}

	return p.addrArr[addrispIndex>>5] + "|" + p.ispArr[addrispIndex&0x001F]
}

func main() {
	phoneSearch := PhoneSearch4Binary{}
	err := phoneSearch.LoadDat()
	if err != nil {
		fmt.Println("Error loading dat file:", err)
		os.Exit(1)
	}

	result := phoneSearch.Query("1319876")
	fmt.Println(result)

	//1319876->四川|自贡|643000|0813|510300|中国联通
}
