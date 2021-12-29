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

use std::{borrow::Cow, sync::{RwLock, RwLockReadGuard, RwLockWriteGuard}};

use rustler::{resource::ResourceArc};
use rustler::{Binary, Encoder, Env, NifResult, OwnedBinary, Term};

use atoms::{ok};
use options::NifgeoipOptions;
use geoip_rust::geoip::GeoIP;
// =================================================================================================
// resource
// =================================================================================================
struct Nifgeoip{
    data: GeoIP
}
impl Nifgeoip{
    // create
    fn new(_: NifgeoipOptions) -> Result<Self, String>{
        let geoip = GeoIP::new();
        match geoip{
            Ok(geoip) =>
            Ok(Nifgeoip{data: geoip}),
            Err(e) =>
            Err(e.to_string())
        }
    }
    // clear
    fn clear(&mut self) {
        drop(self);
    }
    // write
    fn query<'a>(&mut self, ip: &[u8]) -> &'a str {
       let ip = self.u8_to_string(ip);
       self.data.query(ip.as_str())
    }
    fn u8_to_string(&self, msg: &[u8]) -> String{
        let a = String::from_utf8_lossy(msg);
        match a{
            Cow::Owned(own_msg) => own_msg,
            Cow::Borrowed(b_msg) => b_msg.to_string()
        }
    }
}
#[repr(transparent)]
struct NifgeoipResource(RwLock<Nifgeoip>);

impl NifgeoipResource {
    fn read(&self) -> RwLockReadGuard<'_, Nifgeoip> {
        self.0.read().unwrap()
    }

    fn write(&self) -> RwLockWriteGuard<'_, Nifgeoip> {
        self.0.write().unwrap()
    }
}

impl From<Nifgeoip> for NifgeoipResource {
    fn from(other: Nifgeoip) -> Self {
        NifgeoipResource(RwLock::new(other))
    }
}

pub fn on_load(env: Env, _load_info: Term) -> bool {
    rustler::resource!(NifgeoipResource, env);
    true
}

// =================================================================================================
// api
// =================================================================================================

#[rustler::nif]
fn new<'a>(env: Env<'a>, opts: NifgeoipOptions) -> NifResult<Term<'a>> {
    let rs = Nifgeoip::new(opts).map_err(|e| rustler::error::Error::Term(Box::new(e)))?;
    Ok((ok(), ResourceArc::new(NifgeoipResource::from(rs))).encode(env))
}
#[rustler::nif]
fn clear<'a>(env: Env<'a>, resource: ResourceArc<NifgeoipResource>) -> NifResult<Term<'a>> {
    resource.write().clear();
    Ok(ok().encode(env))
}
#[rustler::nif]
fn query<'a>(env: Env<'a>, resource: ResourceArc<NifgeoipResource>, msg: LazyBinary<'a>) -> NifResult<Term<'a>> {
    let mut rs = resource.write();
    let rs = rs.query(&msg);
   Ok(rs.encode(env)) 
}
// =================================================================================================
// helpers
// =================================================================================================

/// Represents either a borrowed `Binary` or `OwnedBinary`.
///
/// `LazyBinary` allows for the most efficient conversion from an
/// Erlang term to a byte slice. If the term is an actual Erlang
/// binary, constructing `LazyBinary` is essentially
/// zero-cost. However, if the term is any other Erlang type, it is
/// converted to an `OwnedBinary`, which requires a heap allocation.
enum LazyBinary<'a> {
    Owned(OwnedBinary),
    Borrowed(Binary<'a>),
}

impl<'a> std::ops::Deref for LazyBinary<'a> {
    type Target = [u8];
    fn deref(&self) -> &[u8] {
        match self {
            Self::Owned(owned) => owned.as_ref(),
            Self::Borrowed(borrowed) => borrowed.as_ref(),
        }
    }
}

impl<'a> rustler::Decoder<'a> for LazyBinary<'a> {
    fn decode(term: Term<'a>) -> NifResult<Self> {
        if term.is_binary() {
            Ok(Self::Borrowed(Binary::from_term(term)?))
        } else {
            Ok(Self::Owned(term.to_binary()))
        }
    }
}
