package main

import (
	"bufio"
	"fmt"
	"os"
	"strings"
	"github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go/qzdb"
)

func main() {
	if len(os.Args) < 2 {
		return
	}
	dbPath := os.Args[1]
	searcher, err := qzdb.Open(dbPath, 0, true)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Load error: %v\n", err)
		return
	}

	scanner := bufio.NewScanner(os.Stdin)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}
		res := searcher.FindStr(line)
		_ = err
		if err != nil {
			fmt.Println("")
		} else {
			fmt.Println(res)
		}
	}
}
