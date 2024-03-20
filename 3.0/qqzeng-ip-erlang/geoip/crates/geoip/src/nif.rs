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

use rustler::types::tuple::make_tuple;
use rustler::Atom;
use std::{
    borrow::Cow,
    sync::{RwLock, RwLockReadGuard, RwLockWriteGuard},
};

use rustler::resource::ResourceArc;
use rustler::{Binary, Encoder, Env, NifResult, OwnedBinary, Term};

use atoms::{error, ok};
use geoip_rust::geoip::GeoIP;
// =================================================================================================
// resource
// =================================================================================================
struct Nifgeoip {
    data: GeoIP,
}
impl Nifgeoip {
    // create
    fn new(file: &[u8]) -> Result<Self, String> {
        let file = Nifgeoip::u8_to_string(file);
        let file = file.as_str();
        let geoip = GeoIP::new(file);
        match geoip {
            Ok(geoip) => Ok(Nifgeoip { data: geoip }),
            Err(e) => Err(e.to_string()),
        }
    }
    // write
    fn query<'a>(&self, ip: &[u8]) -> Result<&'a str, String> {
        let ip = Nifgeoip::u8_to_string(ip);
        match self.data.query(ip.as_str()) {
            Ok(str) => Ok(str),
            Err(e) => Err(e.to_string()),
        }
    }
    fn u8_to_string(msg: &[u8]) -> String {
        let a = String::from_utf8_lossy(msg);
        match a {
            Cow::Owned(own_msg) => own_msg,
            Cow::Borrowed(b_msg) => b_msg.to_string(),
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
fn new<'a>(env: Env<'a>, file: LazyBinary<'a>) -> NifResult<Term<'a>> {
    let rs = Nifgeoip::new(&file).map_err(|e| rustler::error::Error::Term(Box::new(e)))?;
    Ok((ok(), ResourceArc::new(NifgeoipResource::from(rs))).encode(env))
}
#[rustler::nif]
fn clear(env: Env, resource: ResourceArc<NifgeoipResource>) -> NifResult<Term> {
    drop(resource.write());
    Ok(ok().encode(env))
}
#[rustler::nif]
fn query<'a>(
    env: Env<'a>,
    resource: ResourceArc<NifgeoipResource>,
    ip: LazyBinary<'a>,
) -> NifResult<Term<'a>> {
    let rs = resource.read();
    match rs.query(&ip) {
        Ok(rs) => Ok((ok(), rs).encode(env)),
        Err(e) => Ok((error(), e).encode(env)),
    }
}
#[rustler::nif]
fn query_friendly<'a>(
    env: Env<'a>,
    resource: ResourceArc<NifgeoipResource>,
    ip: LazyBinary<'a>,
) -> NifResult<Term<'a>> {
    let rs = resource.read();
    match rs.query(&ip) {
        Ok(rs) => {
            let terms: Vec<_> = rs.split('|').map(|x|x.encode(env)).collect();
            let mut rs: Vec<Term> = vec![Atom::from_str(env,"GeoIPRes").unwrap().encode(env)];
            rs.extend(terms);
            let rs = make_tuple(env, rs.as_ref()).encode(env);
            Ok((ok(), rs).encode(env))},
        Err(e) => Ok((error(), e).encode(env)),
    }
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
