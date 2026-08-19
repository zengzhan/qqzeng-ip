package main

import (
	"bufio"
	"fmt"
	"os"
	"strings"

	"github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go/qzdb"
)

func main() {
	if len(os.Args) < 3 {
		fmt.Fprintln(os.Stderr, "usage: dump <db> <ipfile>")
		os.Exit(2)
	}
	db := os.Args[1]
	ipf := os.Args[2]
	s, err := qzdb.Open(db, 0, true)
	if err != nil {
		fmt.Fprintln(os.Stderr, "load error:", err)
		os.Exit(1)
	}
	f, err := os.Open(ipf)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	defer f.Close()
	sc := bufio.NewScanner(f)
	for sc.Scan() {
		ip := strings.TrimSpace(sc.Text())
		if ip == "" {
			continue
		}
		info, ferr := s.Find(ip)
		if ferr != nil || info == nil {
			fmt.Printf("%s\t__NOTFOUND__\n", ip)
			continue
		}
		parts := make([]string, 0, len(info.FieldNames))
		for _, n := range info.FieldNames {
			parts = append(parts, n+"="+info.Get(n))
		}
		fmt.Printf("%s\t%s\n", ip, strings.Join(parts, "\t"))
	}
}
