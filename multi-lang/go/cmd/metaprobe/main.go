// 元信息探针（Go）：输出与 meta_probe_node.js 同构的 JSON。
package main

import (
	"encoding/json"
	"os"
	"path/filepath"

	"qzdb_reader/qzdb"
)

type row struct {
	File             string   `json:"file"`
	Lang             string   `json:"lang"`
	Edition          string   `json:"edition"`
	EditionSource    string   `json:"edition_source"`
	VersionMask      int      `json:"version_mask"`
	FieldNamesSource string   `json:"field_names_source"`
	FieldNames       []string `json:"field_names"`
	GroupCount       int      `json:"group_count"`
	PoolCount        int      `json:"pool_count"`
	DataMonth        string   `json:"data_month"`
}

func main() {
	out := make([]row, 0, len(os.Args)-1)
	for _, f := range os.Args[1:] {
		r, err := qzdb.Open(f, 0, true)
		if err != nil {
			panic(err)
		}
		out = append(out, row{
			File:             filepath.Base(f),
			Lang:             "go",
			Edition:          r.GetEdition(),
			EditionSource:    r.GetEditionSource(),
			VersionMask:      int(r.GetVersionMask()),
			FieldNamesSource: r.GetFieldNamesSource(),
			FieldNames:       r.GetFieldNames(),
			GroupCount:       r.GetGroupCount(),
			PoolCount:        r.GetPoolCount(),
			DataMonth:        r.GetDataMonth(),
		})
		r.Close()
	}
	enc := json.NewEncoder(os.Stdout)
	enc.SetEscapeHTML(false)
	_ = enc.Encode(out)
}
