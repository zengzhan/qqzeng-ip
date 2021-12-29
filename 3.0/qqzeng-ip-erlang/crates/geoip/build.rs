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
// Created : 2021-12-29T02:32:05+00:00
//-------------------------------------------------------------------

use std::{env, fs::File, io::Write, path::Path};

fn main() {
    // Directory contain this build-script
    let here = env::var("CARGO_MANIFEST_DIR").unwrap();
    let mut libpath = if let Ok(here) = env::var("CARGO_TARGET_DIR") {
        Path::new(&here).to_path_buf()
    } else {
        Path::new(&here).join("target")
    };
    // let here = env::var("CARGO_HOME").unwrap();
    // Host triple (arch of machine doing to build, not necessarily the arch we're building for)
    let host_triple = env::var("HOST").unwrap();
    // Target triple (arch we're building for, not necessarily the arch we're building on)
    let target_triple = env::var("TARGET").unwrap();
    // debug or release
    let profile = env::var("PROFILE").unwrap();
    // We use target OS to determine if extension is `.so`, `.dll`, or `.dylib`
    let file_name = match env::var("CARGO_CFG_TARGET_OS").unwrap().as_str() {
        "windows" => "libgeoip.dll",
        "macos" | "ios" => "libgeoip.dylib",
        _ => "libgeoip.so",
    };
    // Location of libmatcher
    // let mut libpath = Path::new(&here).join("target");
    if host_triple != target_triple {
        libpath = libpath.join(&target_triple);
    }
    libpath = libpath.join(&profile).join(&file_name);

    // Create file in `here` and write the path to the directory of
    // where to find libmatcher
    let libpath_file_path = Path::new(&here).join("libpath");
    let mut libpath_file = File::create(libpath_file_path).unwrap();
    write!(libpath_file, "{}", libpath.to_str().unwrap()).unwrap();
}
