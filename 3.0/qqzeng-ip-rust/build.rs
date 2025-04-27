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
//-------------------------------------------------------------------
extern crate cc;
use std::env;
use std::process::Command;
fn main() {
    let dev = env::var("CC_LOCAL");
    match dev {
        Ok(_) => {
            println!("cargo:return-if-changed=../qqzeng-ip-c");
            cc::Build::new()
                .file("../qqzeng-ip-c/GeoIP.c")
                .compile("libgeoip.a");
        }
        Err(_) => {
            println!("cargo:rerun-if-changed=./qqzeng-ip.git/3.0/qqzeng-ip-c");
            let here = env::var("CARGO_MANIFEST_DIR").unwrap();
            let mut cmd = Command::new("sh");
            let out = cmd
                .arg("-c")
                .arg(format!(
                    " {}/tool.sh 3.0/qqzeng-ip-c https://github\
            .com/yangcancai/qqzeng-ip.git",
                    here
                ))
                .output()
                .expect("git clone qqzeng-ip-c error");
            println!("running: {:?}", cmd);
            let rs: Vec<&str> = out
                .stdout
                .split(|x| *x == '\n' as u8)
                .map(|x| std::str::from_utf8(x).unwrap())
                .collect();
            for i in rs {
                println!("{}", i);
            }
            cc::Build::new()
                .file("qqzeng-ip.git/3.0/qqzeng-ip-c/GeoIP.c")
                .compile("libgeoip.a");
        }
    }
}
