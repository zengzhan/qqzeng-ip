package main

import (
	"bufio"
	"fmt"
	"os"
	"strings"
	"qzdb_searcher/qzdb"
)

func main() {
	if len(os.Args) < 2 {
		return
	}
	dbPath := os.Args[1]
	searcher, err := qzdb.Instance(dbPath)
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
		res, err := searcher.FindStr(line)
		if err != nil {
			fmt.Println("")
		} else {
			fmt.Println(res)
		}
	}
}
