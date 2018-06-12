package main
import	"fmt"
import "./qqzengip"


func main() {

	ipfinder, err := qqzengip.Location()
    if err == nil {
        fmt.Println(ipfinder.Get("8.8.8.8"))
        fmt.Println(ipfinder.Get("114.114.114.114"))
        fmt.Println(ipfinder.Get("255.255.255.255"))
    }
	
	
	//ipsearch2.go
	ipFinder := qqzengip.GetInstance()
	fmt.Println(ipFinder.Get("8.8.8.8"))
	fmt.Println(ipFinder.Get("114.114.114.114"))
	fmt.Println(ipFinder.Get("255.255.255.255"))
}
