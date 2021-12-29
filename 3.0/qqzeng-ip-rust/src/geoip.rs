use std::sync::Arc;
use std::ffi::CStr;
use std::ffi::CString;
use crate::raw::RawGeoIP;
use crate::raw::{geoip_instance,geoip_loadDat, geoip_query};
use anyhow::Result;
use anyhow::Error;
pub struct GeoIP{
    ctx: *mut RawGeoIP
}
unsafe impl Send for GeoIP{}
unsafe impl Sync for GeoIP{}
impl GeoIP{
   pub fn new() ->  Result<Self>{
       unsafe {
           let p = geoip_instance();
           if geoip_loadDat(p) != 0{
             return Err(Error::msg("LoadDat error"));
           }
           Ok(GeoIP{ctx:p})
        }
    }
    pub fn query<'a>(&self, ip: &str) -> &'a str{

        unsafe {
            let ip = CString::new(ip).unwrap();
            let ip = CStr::from_bytes_with_nul(ip.to_bytes_with_nul()).unwrap().as_ptr();
            let ip = geoip_query(self.ctx, ip);
            CStr::from_ptr(ip).to_str().unwrap()
        }
    }
}
impl Drop for GeoIP{
    fn drop(&mut self){
       unsafe {Box::from_raw(self.ctx)};
    }
} 