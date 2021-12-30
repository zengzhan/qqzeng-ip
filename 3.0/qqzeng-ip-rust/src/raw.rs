use std::os::raw::c_char;
#[repr(C)]
pub struct RawGeoIP{
    _private: [u8; 0] 
}
extern "C" {
    pub fn geoip_instance_file(file: *const c_char) -> *mut RawGeoIP;
    pub fn geoip_query(p: *mut RawGeoIP, ip: *const c_char) -> *const c_char;
}
