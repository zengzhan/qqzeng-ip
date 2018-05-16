var ipFinder = require("./IPSearch.js")

ipFinder.load(__dirname+"/qqzeng-ip-3.0-ultimate.dat")

console.time("ip run time");

console.log(ipFinder.query("255.255.255.255"))
console.log(ipFinder.query("221.226.99.130"))
console.log(ipFinder.query("43.245.217.130"))
console.log(ipFinder.query("114.114.114.114"))
console.log(ipFinder.query("180.76.76.76"))
console.log(ipFinder.query("218.205.107.205"))
       
    
console.timeEnd("ip run time");
console.log("ok")
