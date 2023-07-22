package main

import (
	"encoding/json"
	"fmt"
	qqzeng_phone_go "qqzeng_phone_go/phonelookup"
	"strings"
)

func main() {
	phoneSearchBest := qqzeng_phone_go.NewPhoneSearchBest()
	result := phoneSearchBest.Query("16620816699")
	//fmt.Println(result)
	//广东|深圳|518000|0755|440300|联通

	// Split the result string into individual fields
	fields := strings.Split(result, "|")

	// Define a struct to match the format of the result
	type PhoneInfo struct {
		Province string `json:"Province"`
		City     string `json:"City"`
		ZipCode  string `json:"ZipCode"`
		CityCode string `json:"CityCode"`
		AreaCode string `json:"AreaCode"`
		ISP      string `json:"ISP"`
	}

	// Populate the values from the split result
	var info PhoneInfo
	info.Province = fields[0]
	info.City = fields[1]
	info.ZipCode = fields[2]
	info.CityCode = fields[3]
	info.AreaCode = fields[4]
	info.ISP = fields[5]

	// Convert the struct to a JSON byte slice
	jsonData, err := json.Marshal(info)
	if err != nil {
		fmt.Println("Error marshaling to JSON:", err)
		return
	}

	// Print the JSON byte slice
	fmt.Println(string(jsonData))
}
