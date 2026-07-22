/**
 * QzdbSearcher - Go SDK calling example
 *
 * Usage: go run main.go
 * Place qqzeng_ip_std_china.qzdb in the same directory or specify the path.
 */

package main

import (
	"fmt"
	"os"
	"qzdb_searcher/qzdb"
)

func findDb() string {
	candidates := []string{
		"qqzeng_ip_std_china.qzdb",
		"../data/qqzeng_ip_std_china.qzdb",
		"data/qqzeng_ip_std_china.qzdb",
	}
	for _, c := range candidates {
		if _, err := os.Stat(c); err == nil {
			return c
		}
	}
	return ""
}

func main() {
	dbPath := findDb()
	if dbPath == "" {
		fmt.Println("Database file not found")
		return
	}

	searcher, err := qzdb.Instance(dbPath)
	if err != nil {
		fmt.Printf("Failed to load database: %v\n", err)
		return
	}

	fmt.Printf("Version: %s\n", searcher.Version())
	fmt.Printf("Fields (%d): %v\n\n", len(searcher.FieldNames()), searcher.FieldNames())

	// Query sample V4 IPs
	for _, ip := range []string{"114.114.114.114", "223.5.5.5", "8.8.8.8"} {
		result, err := searcher.FindStr(ip)
		if err != nil {
			fmt.Printf("find(\"%-16s\") => error: %v\n", ip, err)
		} else {
			fmt.Printf("find(\"%-16s\") => %s\n", ip, result)
		}
	}

	// Query a V6 IP
	result, err := searcher.FindStr("2408:8000:9000::1")
	if err != nil {
		fmt.Printf("find(\"%-16s\") => error: %v\n", "2408:8000:9000::1", err)
	} else {
		fmt.Printf("find(\"%-16s\") => %s\n", "2408:8000:9000::1", result)
	}

	// Get structured fields
	fmt.Println("\n--- Structured fields for 114.114.114.114 ---")
	loc, err := searcher.Find("114.114.114.114")
	if err != nil {
		fmt.Printf("  Error: %v\n", err)
	} else if loc != nil {
		for i, name := range searcher.FieldNames() {
			fmt.Printf("  %s: %s\n", name, loc.Values[i])
		}
	}
	fmt.Println("TEST_PASS")
}
