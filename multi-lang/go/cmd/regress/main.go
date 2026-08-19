package main

import (
	"bufio"
	"fmt"
	"os"
	"strconv"
	"strings"

	"github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go/qzdb"
)

func main() {
	db := "/tmp/real_asn_china/qzdb/qqzeng_ip_asn_china.qzdb"
	truth := "/tmp/real_asn_china/truth.tsv"
	if len(os.Args) > 1 {
		db = os.Args[1]
	}
	if len(os.Args) > 2 {
		truth = os.Args[2]
	}

	// verifyCrc defaults true via Instance
	s, err := qzdb.Open(db, 0, true)
	if err != nil {
		fmt.Fprintf(os.Stderr, "load failed: %v\n", err)
		os.Exit(2)
	}
	fmt.Printf("loaded fields=%v\n", s.FieldNames())

	f, err := os.Open(truth)
	if err != nil {
		fmt.Fprintf(os.Stderr, "open truth failed: %v\n", err)
		os.Exit(3)
	}
	defer f.Close()

	var checked, exact, notfound, other int64
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 1024*1024), 1024*1024)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line == "" {
			continue
		}
		parts := strings.Split(line, "\t")
		if len(parts) < 3 {
			continue
		}
		ip := parts[0]
		expAsn, _ := strconv.ParseInt(parts[1], 10, 64)

		info, ferr := s.Find(ip)
		if ferr != nil || info == nil {
			notfound++
			continue
		}
		got := info.Get("asn")
		gotAsn, _ := strconv.ParseInt(got, 10, 64)
		checked++
		if gotAsn == expAsn {
			exact++
		} else {
			other++
		}
	}
	if err := sc.Err(); err != nil {
		fmt.Fprintf(os.Stderr, "read truth error: %v\n", err)
		os.Exit(4)
	}

	fmt.Printf("\nGO REAL-DATA REGRESSION (truth.tsv, real qzdb)\n")
	fmt.Printf("  checked=%d EXACT=%d NOTFOUND=%d MISMATCH=%d\n", checked, exact, notfound, other)
	if exact == checked && notfound == 0 {
		fmt.Println("  >>> PASS 100%")
	} else {
		fmt.Println("  >>> FAIL")
		os.Exit(1)
	}
}
