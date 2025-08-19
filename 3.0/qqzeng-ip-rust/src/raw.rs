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

use std::os::raw::c_char;
#[repr(C)]
pub struct RawGeoIP {
    _private: [u8; 0],
}
extern "C" {
    pub fn geoip_instance_file(file: *const c_char) -> *mut RawGeoIP;
    pub fn geoip_query(p: *mut RawGeoIP, ip: *const c_char) -> *const c_char;
}
