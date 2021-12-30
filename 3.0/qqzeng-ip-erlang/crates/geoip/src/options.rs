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

use rustler::{Decoder, NifResult, Term};
use serde::{Deserialize, Serialize};

#[derive(Serialize, Deserialize, PartialEq, Clone, Copy, Debug)]
pub struct NifgeoipOptions {
    pub bitmap_size: Option<usize>,
    pub items_count: Option<usize>,
    pub capacity: Option<usize>,
    pub rotate_at: Option<usize>,
    pub fp_rate: Option<f64>,
}

impl Default for NifgeoipOptions {
    fn default() -> NifgeoipOptions {
        NifgeoipOptions {
            bitmap_size: None,
            items_count: None,
            capacity: None,
            rotate_at: None,
            fp_rate: None,
        }
    }
}

impl<'a> Decoder<'a> for NifgeoipOptions {
    fn decode(term: Term<'a>) -> NifResult<Self> {
        let mut opts = Self::default();
        use rustler::{Error, MapIterator};
        for (key, value) in MapIterator::new(term).ok_or(Error::BadArg)? {
            match key.atom_to_string()?.as_ref() {
                "bitmap_size" => opts.bitmap_size = Some(value.decode()?),
                "items_count" => opts.items_count = Some(value.decode()?),
                "capacity" => opts.capacity = Some(value.decode()?),
                "rotate_at" => opts.rotate_at = Some(value.decode()?),
                "fp_rate" => opts.fp_rate = Some(value.decode()?),
                _ => (),
            }
        }
        Ok(opts)
    }
}
