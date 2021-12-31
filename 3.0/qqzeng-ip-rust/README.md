# geoip_rust

## Install?

```rust
[dependencies]
geoip_rust = {git = "https://github.com/qqzeng-ip/qqzeng-ip.git"}
```

## How to use?

```rust
use geoip;
use anyhow::Error;
let rs = geoip::GeoIP::new("qqzeng-ip-3.0-ultimate.dat");
assert_eq!(rs.is_ok(), true);
if let Ok(rs) = rs {
assert_eq!(rs.query("0.0.0.0").unwrap(), "|保留|||||||||");
assert_eq!(rs.query("0.1.1.1").unwrap(), "|保留|||||||||");
assert_eq!(
rs.query("255.255.255.255").unwrap(),
"|保留|全球|旗舰版||qqzeng-ip||最新版|2021-12-01|880995|"
);
let _invalid = Error::msg("Ip invalid");
assert!(matches!(rs.query("kkk").unwrap_err(), _invalid));
assert!(matches!(rs.query("a.a.a.a").unwrap_err(), _invalid));
assert!(matches!(rs.query("255.256.1.1").unwrap_err(), _invalid));
assert!(matches!(rs.query("-1.25.1.1").unwrap_err(), _invalid));
// query_friendly
assert_eq!(
rs.query_friendly("255.255.255.255").unwrap(),
geoip::GeoIPRes {
continent: "",
country: "保留",
province: "全球",
city: "旗舰版",
district: "",
isp: "qqzeng-ip",
area_code: "",
country_english: "最新版",
country_code: "2021-12-01",
longitude: "880995",
latitude: "",
}
);
```