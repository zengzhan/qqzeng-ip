package main

import (
    "fmt"
    "os"
    "qzdb_searcher/qzdb"
)

func main() {
    if len(os.Args) < 3 {
        fmt.Fprintln(os.Stderr, "Usage: test_v20_helper <db_path> <ip1> [ip2 ...]")
        os.Exit(1)
    }
    dbPath := os.Args[1]
    ips := os.Args[2:]
    s, err := qzdb.NewSearcherV20(dbPath, 0)
    if err != nil {
        fmt.Fprintln(os.Stderr, "Failed to load DB:", err)
        os.Exit(1)
    }
    for _, ip := range ips {
        fmt.Println(s.LookupStr(ip))
    }
}
