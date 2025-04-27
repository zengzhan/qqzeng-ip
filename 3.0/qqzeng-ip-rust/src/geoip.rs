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

use crate::raw::RawGeoIP;
use crate::raw::{geoip_instance_file, geoip_query};
use anyhow::Error;
use anyhow::Result;
use std::ffi::CStr;
use std::ffi::CString;
#[derive(Debug)]
pub struct GeoIP {
    ctx: *mut RawGeoIP,
}
#[derive(Debug, Clone, PartialEq)]
pub struct GeoIPRes<'a> {
    pub continent: &'a str,
    pub country: &'a str,
    pub province: &'a str,
    pub city: &'a str,
    pub district: &'a str,
    pub isp: &'a str,
    pub area_code: &'a str,
    pub country_english: &'a str,
    pub country_code: &'a str,
    pub longitude: &'a str,
    pub latitude: &'a str,
}
unsafe impl Send for GeoIP {}
unsafe impl Sync for GeoIP {}
impl GeoIP {
    pub fn new(file: &str) -> Result<Self> {
        unsafe {
            println!("GeoIP: before geoip_instance");
            let file = CString::new(file)?;
            let file = CStr::from_bytes_with_nul(file.to_bytes_with_nul())?.as_ptr();
            let p = geoip_instance_file(file);
            if p.is_null() {
                Err(Error::msg("LoadDat error"))
            } else {
                Ok(GeoIP { ctx: p })
            }
        }
    }
    pub fn query<'a>(&self, ip: &str) -> Result<&'a str> {
        if let Ok(true) = self.is_valid_ip(ip) {
            unsafe {
                let ip = CString::new(ip)?;
                let ip = CStr::from_bytes_with_nul(ip.to_bytes_with_nul())?.as_ptr();
                let ip = geoip_query(self.ctx, ip);
                Ok(CStr::from_ptr(ip).to_str()?)
            }
        } else {
            Err(Error::msg("Ip invalid"))
        }
    }
    pub fn query_friendly<'a>(&self, ip: &str) -> Result<GeoIPRes<'a>> {
        match self.query(ip) {
            Ok(res) => {
                let res: Vec<&str> = res.split('|').collect();
                if res.len() == 11 {
                    Ok(GeoIPRes {
                        continent: res[0],
                        country: res[1],
                        province: res[2],
                        city: res[3],
                        district: res[4],
                        isp: res[5],
                        area_code: res[6],
                        country_english: res[7],
                        country_code: res[8],
                        longitude: res[9],
                        latitude: res[10],
                    })
                } else {
                    Err(Error::msg("GeoIPInfo format error"))
                }
            }
            Err(e) => Err(e),
        }
    }
    pub fn is_valid_ip(&self, ip: &str) -> Result<bool> {
        let ip: Vec<&str> = ip.split('.').collect();
        if ip.len() != 4 {
            return Ok(false);
        }
        for sub in ip {
            let sub: i32 = sub.parse()?;
            if !(0..=255).contains(&sub) {
                return Ok(false);
            }
        }
        Ok(true)
    }
}
impl Drop for GeoIP {
    fn drop(&mut self) {
        unsafe { let _= Box::from_raw(self.ctx); };
    }
}
