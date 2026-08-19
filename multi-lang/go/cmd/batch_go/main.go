// Batch IP query runner for Go
// Build: go build -o batch_go batch_query.go
// Usage: ./batch_go <database_path> <v4_test> <v4_output> <v6_test> <v6_output>
package main

import (
	"fmt"
	"os"
	"strconv"
	"strings"
	"github.com/zengzhan/qqzeng-ip/ip-qzdb-sdk/go/qzdb"
)

// geoToPipe uses GeoInfo.ToPipe() so output byte-matches Python to_pipe()
func geoToPipe(info *qzdb.GeoInfo) string {
	if info == nil {
		return ""
	}
	return info.ToPipe()
}

// parseV4Key parses a V4 key: "a.b.c.d" dotted-quad or decimal u32.
func parseV4Key(s string) (uint32, bool) {
	if parts := strings.Split(s, "."); len(parts) == 4 {
		var v uint32
		for _, p := range parts {
			octet, err := strconv.ParseUint(p, 10, 8)
			if err != nil {
				return 0, false
			}
			v = v<<8 | uint32(octet)
		}
		return v, true
	}
	v, err := strconv.ParseUint(s, 10, 32)
	if err != nil {
		return 0, false
	}
	return uint32(v), true
}

func processFile(searcher *qzdb.QzdbReader, testPath, outPath string, isV6 bool) int {
	data, err := os.ReadFile(testPath)
	if err != nil {
		return 0
	}
	lines := strings.Split(strings.TrimSpace(string(data)), "\n")
	
	var results []string
	for _, line := range lines {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		var pipeStr string
		if isV6 {
			parts := strings.Split(line, ":")
			if len(parts) != 2 {
				continue
			}
			high, _ := strconv.ParseUint(parts[0], 10, 64)
			low, _ := strconv.ParseUint(parts[1], 10, 64)
			var ip16 [16]byte
			for i := 0; i < 8; i++ {
				ip16[7-i] = byte(high >> (8 * i))
				ip16[15-i] = byte(low >> (8 * i))
			}
			info, err := searcher.FindV6Uint(ip16)
			if err != nil {
				pipeStr = ""
			} else {
				pipeStr = geoToPipe(info)
			}
		} else {
			ip, ok := parseV4Key(line)
			if !ok {
				results = append(results, fmt.Sprintf("%s|", line))
				continue
			}
			info, err := searcher.FindUint(ip)
			if err != nil {
				pipeStr = ""
			} else {
				pipeStr = geoToPipe(info)
			}
		}
		results = append(results, fmt.Sprintf("%s|%s", line, pipeStr))
	}
	
	os.WriteFile(outPath, []byte(strings.Join(results, "\n")+"\n"), 0644)
	return len(results)
}

func main() {
	if len(os.Args) < 5 {
		fmt.Fprintf(os.Stderr, "Usage: %s <db_path> <v4_test> <v4_out> <v6_test> <v6_out>\n", os.Args[0])
		os.Exit(1)
	}
	
	dbPath := os.Args[1]
	v4Test := os.Args[2]
	v4Out := os.Args[3]
	v6Test := os.Args[4]
	v6Out := os.Args[5]
	
	searcher, err := qzdb.Open(dbPath, 0, false)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Go: Failed to load database: %v\n", err)
		os.Exit(1)
	}
	defer searcher.Close()
	
	n4 := processFile(searcher, v4Test, v4Out, false)
	fmt.Fprintf(os.Stderr, "  Go V4: %d queries\n", n4)
	
	n6 := processFile(searcher, v6Test, v6Out, true)
	fmt.Fprintf(os.Stderr, "  Go V6: %d queries\n", n6)
	
	fmt.Fprintf(os.Stderr, "  Go DONE\n")
}
