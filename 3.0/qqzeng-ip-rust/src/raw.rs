use std::os::raw::c_char;
#[repr(C)]
pub struct RawGeoIP;
extern {
    pub fn geoip_instance() -> *mut RawGeoIP;
    pub fn geoip_loadDat(p: *mut RawGeoIP) -> libc::c_int;
	pub fn geoip_query(p: *mut RawGeoIP, ip: * const c_char) -> * const c_char;
}