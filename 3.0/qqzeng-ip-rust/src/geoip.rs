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
unsafe impl Send for GeoIP {}
unsafe impl Sync for GeoIP {}
impl GeoIP {
    pub fn new(file: &str) -> Result<Self> {
        unsafe {
            println!("GeoIP: before geoip_instance");
            let file = CString::new(file)?;
            let file = CStr::from_bytes_with_nul(file.to_bytes_with_nul())?.as_ptr();
            let p = geoip_instance_file(file);
            if p.is_null(){
                Err(Error::msg("LoadDat error"))
            }else{
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
    pub fn is_valid_ip(&self, ip: &str) -> Result<bool> {
        let ip: Vec<&str> = ip.split('.').collect();
        if ip.len() != 4{
            return Ok(false);
        }
        for sub in ip{
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
        unsafe { Box::from_raw(self.ctx) };
    }
}
