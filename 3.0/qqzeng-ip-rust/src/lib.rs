//-------------------------------------------------------------------
// @author yangcancai

// Copyright (c) 2021 by yangcancai(yangcancai0112@gmail.com), All Rights Reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//       https://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//

// @doc
//
// @end
// Created : 2021-12-29T02:56:42+00:00

extern crate anyhow;
extern crate libc;
pub mod geoip;
mod raw;
#[cfg(test)]
mod tests {
    use crate::geoip::GeoIPRes;

    use super::*;
    use anyhow::Error;
    #[test]
    fn it_works() {
        let rs = geoip::GeoIP::new("qqzeng-ip-3.0-ultimate.dat");
        assert_eq!(rs.is_ok(), true);
        if let Ok(rs) = rs {
            assert_eq!(rs.query("0.0.0.0").unwrap(), "|保留||||本机网络|||||");
            assert_eq!(rs.query("0.1.1.1").unwrap(), "|保留||||本机网络|||||");
            assert_eq!(
                rs.query("255.255.255.255").unwrap(),
                "|保留|全球|旗舰版||qqzeng-ip||最新版|1197377|2025-02-01|"
            );
            let _invalid = Error::msg("Ip invalid");
            assert!(matches!(rs.query("kkk").unwrap_err(), _invalid));
            assert!(matches!(rs.query("a.a.a.a").unwrap_err(), _invalid));
            assert!(matches!(rs.query("255.256.1.1").unwrap_err(), _invalid));
            assert!(matches!(rs.query("-1.25.1.1").unwrap_err(), _invalid));
            assert_eq!(
                rs.query_friendly("103.235.46.115").unwrap(),
                GeoIPRes {
                    continent: "亚洲",
                    country: "中国",
                    province: "香港",
                    city: "",
                    district: "",
                    isp: "百度",
                    area_code: "810000",
                    country_english: "Hongkong,China",
                    country_code: "HK",
                    longitude: "114.1734",
                    latitude: "22.3200"
                }
            );

            assert_eq!(
                rs.query_friendly("8.7.6.5").unwrap(),
                GeoIPRes {
                    continent: "北美洲",
                    country: "美国",
                    province: "加利福尼亚州",
                    city: "",
                    district: "",
                    isp: "Level3",
                    area_code: "",
                    country_english: "United States",
                    country_code: "US",
                    longitude: "-119.418",
                    latitude: "36.778"
                }
            );
            // query_friendly
            assert_eq!(
                rs.query_friendly("255.255.255.255").unwrap(),
                GeoIPRes {
                    continent: "",
                    country: "保留",
                    province: "全球",
                    city: "旗舰版",
                    district: "",
                    isp: "qqzeng-ip",
                    area_code: "",
                    country_english: "最新版",
                    country_code: "1197377",
                    longitude: "2025-02-01",
                    latitude: ""
                }
            );
        }
    }
    #[test]
    fn it_fail() {
        let rs = geoip::GeoIP::new("");
        let _invalid = Error::msg("LoadDat error");
        assert!(matches!(rs.unwrap_err(), _invalid));
    }
}
