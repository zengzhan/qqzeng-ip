use memmap2::Mmap;
use std::collections::HashMap;
use std::fs;
use std::net::Ipv6Addr;
use std::sync::OnceLock;

const SENTINEL: u32 = 0x80000000;

static INSTANCE: OnceLock<QzdbSearcher> = OnceLock::new();

fn crc32_compute(data: &[u8]) -> u32 {
    let table = crc32_table();
    let mut crc: u32 = 0xFFFFFFFF;
    for &b in data {
        crc = table[((crc ^ b as u32) & 0xFF) as usize] ^ (crc >> 8);
    }
    crc ^ 0xFFFFFFFF
}

fn crc32_table() -> [u32; 256] {
    let mut table = [0u32; 256];
    for i in 0..256u32 {
        let mut c = i;
        for _ in 0..8 {
            if c & 1 != 0 {
                c = (c >> 1) ^ 0xEDB88320;
            } else {
                c >>= 1;
            }
        }
        table[i as usize] = c;
    }
    table
}

#[derive(Debug, Clone)]
pub struct GeoInfo {
    pub fields: HashMap<String, String>,
    pub field_names: Vec<String>,
    pub float_field_indices: Vec<usize>,
}

impl GeoInfo {
    pub fn get(&self, name: &str) -> &str {
        self.fields.get(name).map(|s| s.as_str()).unwrap_or("")
    }

    pub fn to_pipe(&self) -> String {
        let mut parts = Vec::with_capacity(self.field_names.len());
        for (i, fname) in self.field_names.iter().enumerate() {
            let val = self.fields.get(fname).cloned().unwrap_or_default();
            if self.float_field_indices.contains(&i) && !val.is_empty() {
                if let Ok(f) = val.parse::<f64>() {
                    parts.push(format!("{:.6}", f));
                    continue;
                }
            }
            parts.push(val);
        }
        parts.join("|")
    }
}

pub struct QzdbSearcher {
    data: Mmap,
    group_index: usize,
    field_names: Vec<String>,
    float_field_indices: Vec<usize>,
    version_name: String,

    // Header fields
    flags: u16,
    has_v4: bool,
    has_v6: bool,
    v4_node_24: bool,
    v6_node_24: bool,
    v6_jump_bits: usize,
    pool_count: usize,
    pool_idx_size: usize,
    geo_count: usize,
    row_count: usize,
    v4_rec_count: u32,
    v6_rec_count: u32,
    v4_node_count: u32,
    v6_node_count: u32,
    ip_row_size: usize,
    geo_entry_group_count: usize,

    // Offsets
    off_v4_jump: u64,
    off_v4_nodes: u64,
    off_v6_jump: u64,
    off_v6_nodes: u64,
    off_ip_row: u64,
    off_geo_entries: u64,
    off_pools: u64,
    off_meta: u64,
    off_row_schema: u64,
    off_group_schema: u64,

    // Schema and layout cache
    group_field_counts: Vec<usize>,
    group_entry_counts: Vec<u32>,
    group_dim_masks: Vec<u16>,
    group_entry_offsets: Vec<u64>,

    group_strides: Vec<usize>,
    group_field_widths: Vec<Vec<usize>>,
    group_field_offsets: Vec<Vec<usize>>,
    group_field_native: Vec<Vec<bool>>,
    group_field_native_type: Vec<Vec<usize>>,

    group_pools: OnceLock<Vec<Vec<Vec<String>>>>,
}

#[inline(always)]
fn read_u16_le(data: &[u8], off: usize) -> u16 {
    u16::from_le_bytes(data[off..off + 2].try_into().unwrap())
}

#[inline(always)]
fn read_u32_le(data: &[u8], off: usize) -> u32 {
    u32::from_le_bytes(data[off..off + 4].try_into().unwrap())
}

#[inline(always)]
fn read_u64_le(data: &[u8], off: usize) -> u64 {
    u64::from_le_bytes(data[off..off + 8].try_into().unwrap())
}

#[inline(always)]
fn read_u24_le(data: &[u8], off: usize) -> u32 {
    data[off] as u32 | (data[off + 1] as u32) << 8 | (data[off + 2] as u32) << 16
}

#[inline(always)]
fn read_u48_le(data: &[u8], off: usize) -> u64 {
    data[off] as u64
        | (data[off + 1] as u64) << 8
        | (data[off + 2] as u64) << 16
        | (data[off + 3] as u64) << 24
        | (data[off + 4] as u64) << 32
        | (data[off + 5] as u64) << 40
}

pub fn instance(db_path: &str) -> &'static QzdbSearcher {
    INSTANCE.get_or_init(|| {
        from_file(db_path)
    })
}

pub fn from_file(db_path: &str) -> QzdbSearcher {
    let file = fs::File::open(db_path).expect("Failed to open database");
    let mmap = unsafe { Mmap::map(&file).expect("Failed to mmap database") };
    QzdbSearcher::new(mmap, 0)
}

impl QzdbSearcher {
    pub fn new(data: Mmap, group_index: usize) -> Self {
        if data.len() < 192 {
            panic!("File too small for QZDB header");
        }

        let magic = &data[..4];
        if magic != b"QZDB" {
            panic!("Invalid magic, expected QZDB");
        }

        let fmt_ver = data[4];
        if fmt_ver < 1 || fmt_ver > 6 {
            panic!("Unsupported format version: {}", fmt_ver);
        }

        let flags = read_u16_le(&data, 8);
        let has_v4 = flags & 1 != 0;
        let has_v6 = flags & 2 != 0;
        let v4_node_24 = flags & 0x10 != 0;
        let v6_node_24 = flags & 0x20 != 0;

        let mut v6_jump_bits = data[11] as usize;
        if v6_jump_bits == 0 {
            v6_jump_bits = 16;
        }

        let pool_count = data[12] as usize;
        let pool_idx_size = data[13] as usize;
        let geo_count = read_u16_le(&data, 14) as usize;
        let row_count = read_u32_le(&data, 20) as usize;
        let v4_rec_count = read_u32_le(&data, 24);
        let v6_rec_count = read_u32_le(&data, 28);

        let hs = read_u32_le(&data, 36);
        if hs != 192 {
            panic!("Unexpected header size: {}", hs);
        }

        let off_row_schema = read_u64_le(&data, 40);
        let off_group_schema = read_u64_le(&data, 48);
        let off_v4_jump = read_u64_le(&data, 64);
        let off_v4_nodes = read_u64_le(&data, 72);
        let off_v6_jump = read_u64_le(&data, 80);
        let off_v6_nodes = read_u64_le(&data, 88);
        let off_ip_row = read_u64_le(&data, 96);
        let off_geo_entries = read_u64_le(&data, 104);
        let off_pools = read_u64_le(&data, 136);
        let off_meta = read_u64_le(&data, 144);

        let v4_node_count = read_u32_le(&data, 152);
        let v6_node_count = read_u32_le(&data, 156);
        let ip_row_size = read_u32_le(&data, 160) as usize;
        let geo_entry_group_count = read_u32_le(&data, 164) as usize;

        let mut group_entry_offsets = Vec::with_capacity(4);
        for i in 0..4 {
            group_entry_offsets.push(read_u48_le(&data, 168 + i * 6));
        }

        let mut gm_off = off_geo_entries;
        let group_count = data[gm_off as usize] as usize;
        gm_off += 1;

        let actual_groups = group_count.min(1.max(geo_entry_group_count));
        let mut group_field_counts = vec![0; actual_groups];
        let mut group_entry_counts = vec![0; actual_groups];
        let mut group_dim_masks = vec![0; actual_groups];

        for gi in 0..actual_groups {
            group_field_counts[gi] = data[gm_off as usize] as usize;
            gm_off += 1;
            if fmt_ver == 1 || fmt_ver >= 4 {
                group_entry_counts[gi] = read_u32_le(&data, gm_off as usize);
                gm_off += 4;
            } else {
                group_entry_counts[gi] = read_u16_le(&data, gm_off as usize) as u32;
                gm_off += 2;
            }

            if fmt_ver == 1 || fmt_ver >= 3 {
                group_dim_masks[gi] = read_u16_le(&data, gm_off as usize);
                gm_off += 2;
            } else {
                group_dim_masks[gi] = if gi != 2 { 0x01 } else { 0x02 };
            }
        }

        let mut group_strides = vec![0; actual_groups];
        let mut group_field_widths = vec![None; actual_groups];
        let mut group_field_offsets = vec![None; actual_groups];
        let mut group_field_native = vec![None; actual_groups];
        let mut group_field_native_type = vec![None; actual_groups];

        if off_group_schema > 0 {
            let mut sp = off_group_schema as usize;
            let gs_group_count = read_u16_le(&data, sp) as usize;
            sp += 2;
            let max_gs_groups = gs_group_count.min(actual_groups);
            for gi in 0..max_gs_groups {
                sp += 2; // skip groupId
                let fld_count = read_u16_le(&data, sp) as usize;
                sp += 2;
                sp += 4; // skip entryCount
                let stride = read_u32_le(&data, sp) as usize;
                sp += 4;
                sp += 4; // skip flags

                if gi < actual_groups {
                    group_strides[gi] = stride;
                    let mut widths = vec![0; fld_count];
                    let mut offsets = vec![0; fld_count];
                    let mut natives = vec![false; fld_count];
                    let mut nat_types = vec![0; fld_count];
                    for fi in 0..fld_count {
                        sp += 2; // skip fieldId
                        widths[fi] = data[sp] as usize;
                        sp += 1;
                        let field_flags = data[sp];
                        sp += 1;
                        natives[fi] = (field_flags & 0x01) != 0;
                        nat_types[fi] = ((field_flags >> 1) & 0x03) as usize;
                        offsets[fi] = read_u32_le(&data, sp) as usize;
                        sp += 4;
                        sp += 4; // skip poolSectionId
                    }
                    group_field_widths[gi] = Some(widths);
                    group_field_offsets[gi] = Some(offsets);
                    group_field_native[gi] = Some(natives);
                    group_field_native_type[gi] = Some(nat_types);
                } else {
                    sp += fld_count * 12;
                }
            }
        }

        for g in 0..actual_groups {
            if group_strides[g] == 0 {
                group_strides[g] = group_field_counts[g] * pool_idx_size;
            }
            if group_field_widths[g].is_none() {
                group_field_widths[g] = Some(vec![pool_idx_size; group_field_counts[g]]);
            }
            if group_field_offsets[g].is_none() {
                group_field_offsets[g] = Some((0..group_field_counts[g]).map(|i| i * pool_idx_size).collect());
            }
            if group_field_native[g].is_none() {
                group_field_native[g] = Some(vec![false; group_field_counts[g]]);
            }
            if group_field_native_type[g].is_none() {
                group_field_native_type[g] = Some(vec![0; group_field_counts[g]]);
            }
        }

        let mut s = QzdbSearcher {
            data,
            group_index,
            field_names: Vec::new(),
            float_field_indices: Vec::new(),
            version_name: String::new(),
            flags,
            has_v4,
            has_v6,
            v4_node_24,
            v6_node_24,
            v6_jump_bits,
            pool_count,
            pool_idx_size,
            geo_count,
            row_count,
            v4_rec_count,
            v6_rec_count,
            v4_node_count,
            v6_node_count,
            ip_row_size,
            geo_entry_group_count,
            off_v4_jump,
            off_v4_nodes,
            off_v6_jump,
            off_v6_nodes,
            off_ip_row,
            off_geo_entries,
            off_pools,
            off_meta,
            off_row_schema,
            off_group_schema,
            group_field_counts,
            group_entry_counts,
            group_dim_masks,
            group_entry_offsets,
            group_strides,
            group_field_widths: group_field_widths.into_iter().map(|o| o.unwrap()).collect(),
            group_field_offsets: group_field_offsets.into_iter().map(|o| o.unwrap()).collect(),
            group_field_native: group_field_native.into_iter().map(|o| o.unwrap()).collect(),
            group_field_native_type: group_field_native_type.into_iter().map(|o| o.unwrap()).collect(),
            group_pools: OnceLock::new(),
        };
        s.resolve_field_names();
        s
    }

    fn resolve_field_names(&mut self) {
        let d = &self.data;
        let off_meta = self.off_meta;
        if (self.flags & 4) != 0 && off_meta > 0 && off_meta + 4 <= d.len() as u64 {
            let mut field_names: Option<Vec<String>> = None;
            let mut pos = off_meta as usize;
            while pos + 4 <= d.len() {
                let t = d[pos];
                let length = read_u16_le(d, pos + 2) as usize;
                if t == 0 || length == 0 {
                    break;
                }
                let val = std::str::from_utf8(&d[pos + 4..pos + 4 + length]).unwrap_or("").to_string();
                if t == 1 {
                    self.version_name = val;
                } else if t == 2 {
                    field_names = Some(val.split('|').map(|s| s.to_string()).collect());
                }
                pos += 4 + length;
            }

            if let Some(names) = field_names {
                if names.len() == self.group_field_counts[0] {
                    self.field_names = names;
                    self.float_field_indices = self.field_names.iter().enumerate()
                        .filter(|(_, n)| *n == "longitude" || *n == "latitude")
                        .map(|(i, _)| i).collect();
                    return;
                }
            }
        }

        self.field_names = (0..self.group_field_counts[0]).map(|i| format!("field_{}", i)).collect();
        self.float_field_indices = Vec::new();
    }

    fn ensure_pools_loaded(&self) -> &Vec<Vec<Vec<String>>> {
        self.group_pools.get_or_init(|| {
            let group_count = self.group_field_counts.len();
            let mut group_pools = vec![Vec::new(); group_count];
            if self.off_pools <= 0 {
                return group_pools;
            }

            let pool_end = if self.off_meta > 0 { self.off_meta as usize } else { self.data.len() };
            let mut pool_cursor = self.off_pools as usize;
            let d = &self.data;

            for g in 0..group_count {
                let field_count = self.group_field_counts[g];
                let mut group_pool_list = Vec::with_capacity(field_count);
                let natives = &self.group_field_native[g];
                for f in 0..field_count {
                    if natives[f] {
                        group_pool_list.push(Vec::new());
                        continue;
                    }

                    if pool_cursor + 4 > pool_end {
                        group_pool_list.push(Vec::new());
                        continue;
                    }
                    let count = read_u32_le(d, pool_cursor) as usize;
                    pool_cursor += 4;
                    if self.off_row_schema > 0 {
                        pool_cursor += 4;
                    }
                    if count == 0 {
                        group_pool_list.push(Vec::new());
                        continue;
                    }

                    let mut offsets = Vec::with_capacity(count + 1);
                    for _ in 0..=count {
                        offsets.push(read_u32_le(d, pool_cursor) as usize);
                        pool_cursor += 4;
                    }

                    let mut strings = vec![String::new(); count];
                    for s in 0..count {
                        let start = offsets[s];
                        let end = offsets[s + 1];
                        let length = end - start;
                        if length > 0 {
                            strings[s] = std::str::from_utf8(&d[pool_cursor + start..pool_cursor + end])
                                .unwrap_or("").to_string();
                        }
                    }
                    pool_cursor += offsets[count];
                    group_pool_list.push(strings);
                }
                group_pools[g] = group_pool_list;
            }
            group_pools
        })
    }

    fn read_uint_width(&self, off: usize, width: usize) -> u32 {
        if width <= 1 {
            self.data[off] as u32
        } else if width == 2 {
            read_u16_le(&self.data, off) as u32
        } else if width == 3 {
            read_u24_le(&self.data, off)
        } else {
            read_u32_le(&self.data, off)
        }
    }

    fn get_v4_child(&self, node_idx: u32, bit: u32) -> u32 {
        if self.v4_node_24 {
            let node_offset = self.off_v4_nodes as usize + node_idx as usize * 6;
            let offset = if bit == 0 { node_offset } else { node_offset + 3 };
            let val = self.data[offset] as u32 | (self.data[offset + 1] as u32) << 8 | (self.data[offset + 2] as u32) << 16;
            if val & 0x800000 != 0 {
                return (val & 0x7FFFFF) | SENTINEL;
            }
            val
        } else {
            let child_off = self.off_v4_nodes as usize + node_idx as usize * 8 + bit as usize * 4;
            read_u32_le(&self.data, child_off)
        }
    }

    fn get_v6_child(&self, node_idx: u32, bit: u32) -> u32 {
        if self.v6_node_24 {
            let node_offset = self.off_v6_nodes as usize + node_idx as usize * 6;
            let offset = if bit == 0 { node_offset } else { node_offset + 3 };
            let val = self.data[offset] as u32 | (self.data[offset + 1] as u32) << 8 | (self.data[offset + 2] as u32) << 16;
            if val & 0x800000 != 0 {
                return (val & 0x7FFFFF) | SENTINEL;
            }
            val
        } else {
            let child_off = self.off_v6_nodes as usize + node_idx as usize * 8 + bit as usize * 4;
            read_u32_le(&self.data, child_off)
        }
    }

    fn trie_walk_v4(&self, ip_int: u32) -> u32 {
        let hi16 = ((ip_int >> 16) & 0xFFFF) as usize;
        let ptr = read_u32_le(&self.data, (self.off_v4_jump as usize) + hi16 * 4);

        if ptr == 0 {
            return 0;
        }
        if ptr & SENTINEL != 0 {
            return ptr & 0x7FFFFFFF;
        }

        let mut idx = ptr;
        let mut suffix = (ip_int & 0xFFFF) << 16;

        loop {
            let bit = (suffix >> 31) & 1;
            let child = self.get_v4_child(idx, bit);

            if child == 0 {
                return 0;
            }
            if child & SENTINEL != 0 {
                return child & 0x7FFFFFFF;
            }

            idx = child;
            suffix <<= 1;
        }
    }

    fn trie_walk_v6(&self, ip_int: u128) -> u32 {
        let shift = 128 - self.v6_jump_bits;
        let idx_jump = ((ip_int >> shift) & ((1 << self.v6_jump_bits) - 1)) as usize;
        let ptr = read_u32_le(&self.data, (self.off_v6_jump as usize) + idx_jump * 4);

        if ptr == 0 {
            return 0;
        }
        if ptr & SENTINEL != 0 {
            return ptr & 0x7FFFFFFF;
        }

        let mut idx = ptr;
        let mut depth = self.v6_jump_bits;

        while depth < 128 {
            let bit = ((ip_int >> (127 - depth)) & 1) as u32;
            let child = self.get_v6_child(idx, bit);

            if child == 0 {
                return 0;
            }
            if child & SENTINEL != 0 {
                return child & 0x7FFFFFFF;
            }

            idx = child;
            depth += 1;
        }

        0
    }

    fn read_ip_row(&self, row_id: u32) -> (u32, u32, u32) {
        if row_id == 0 || row_id >= self.row_count as u32 {
            return (0, 0, 0);
        }
        let off = (self.off_ip_row as usize) + (row_id as usize) * self.ip_row_size;
        let geo_id = read_u24_le(&self.data, off);
        let asn_id = read_u24_le(&self.data, off + 3);

        let mut usage_type_id = 0;
        if self.ip_row_size >= 9 {
            usage_type_id = read_u24_le(&self.data, off + 6);
        }

        (geo_id, asn_id, usage_type_id)
    }

    fn resolve_row_id(&self, row_id: u32, group_index: usize) -> Option<GeoInfo> {
        let (geo_id, asn_id, usage_type_id) = self.read_ip_row(row_id);
        let mask = if group_index < self.group_dim_masks.len() {
            self.group_dim_masks[group_index]
        } else {
            0
        };

        let entry_id = if mask & 0x02 != 0 {
            asn_id
        } else if mask & 0x04 != 0 {
            usage_type_id
        } else {
            geo_id
        };

        if entry_id == 0 {
            return None;
        }
        self.resolve_geo(entry_id, group_index)
    }

    fn resolve_geo(&self, entry_id: u32, group_index: usize) -> Option<GeoInfo> {
        if group_index >= self.group_field_counts.len() {
            return None;
        }
        if entry_id >= self.group_entry_counts[group_index] {
            return None;
        }

        let pools = self.ensure_pools_loaded();
        let field_count = self.group_field_counts[group_index];
        if field_count == 0 {
            return None;
        }

        let group_entry_start = self.off_geo_entries + self.group_entry_offsets[group_index];
        let stride = self.group_strides[group_index];
        let entry_offset = group_entry_start as usize + (entry_id as usize) * stride;
        let d = &self.data;

        let widths = &self.group_field_widths[group_index];
        let base_offsets = &self.group_field_offsets[group_index];
        let natives = &self.group_field_native[group_index];
        let nat_types = &self.group_field_native_type[group_index];

        let mut fields = HashMap::with_capacity(field_count);
        for i in 0..field_count {
            let w = widths[i];
            let fo = entry_offset + base_offsets[i];
            let is_native = natives[i];

            let val = if is_native {
                let t = nat_types[i];
                if t == 1 {
                    if w == 4 {
                        let bits = read_u32_le(d, fo);
                        f32::from_bits(bits).to_string()
                    } else {
                        let bits = read_u64_le(d, fo);
                        f64::from_bits(bits).to_string()
                    }
                } else {
                    let val_num = self.read_uint_width(fo, w);
                    val_num.to_string()
                }
            } else {
                let idx = self.read_uint_width(fo, w) as usize;
                let group_pool = &pools[group_index];
                if i < group_pool.len() && idx < group_pool[i].len() {
                    group_pool[i][idx].clone()
                } else {
                    String::new()
                }
            };

            let fname = if i < self.field_names.len() {
                self.field_names[i].clone()
            } else {
                format!("field_{}", i)
            };
            fields.insert(fname, val);
        }

        Some(GeoInfo {
            fields,
            field_names: self.field_names.clone(),
            float_field_indices: self.float_field_indices.clone(),
        })
    }

    pub fn find(&self, ip_str: &str) -> Option<GeoInfo> {
        if ip_str.is_empty() {
            return None;
        }

        if ip_str.contains(':') {
            let addr: Ipv6Addr = ip_str.parse().ok()?;
            let octets = addr.octets();
            
            // Check for IPv4-mapped IPv6 (::ffff:x.x.x.x)
            if octets[..12] == [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff] {
                let v4 = u32::from_be_bytes([octets[12], octets[13], octets[14], octets[15]]);
                return self.find_uint(v4);
            }

            let mut val: u128 = 0;
            for &o in &octets {
                val = (val << 8) | o as u128;
            }
            self.find_v6(val)
        } else {
            self.find_uint(fast_parse_ip_v4(ip_str)?)
        }
    }

    pub fn find_uint(&self, ip_int: u32) -> Option<GeoInfo> {
        if !self.has_v4 {
            return None;
        }
        let row_id = self.trie_walk_v4(ip_int);
        if row_id == 0 {
            return None;
        }
        self.resolve_row_id(row_id, self.group_index)
    }

    pub fn find_v6(&self, ip_int: u128) -> Option<GeoInfo> {
        if !self.has_v6 {
            return None;
        }
        let row_id = self.trie_walk_v6(ip_int);
        if row_id == 0 {
            return None;
        }
        self.resolve_row_id(row_id, self.group_index)
    }

    pub fn find_str(&self, ip_str: &str) -> String {
        self.find(ip_str).map(|info| info.to_pipe()).unwrap_or_default()
    }

    pub fn field_names(&self) -> &[String] {
        &self.field_names
    }

    pub fn float_indices(&self) -> &[usize] {
        &self.float_field_indices
    }

    pub fn version_code(&self) -> u8 {
        match self.pool_count {
            6 => 1,
            7 => 2,
            25 => 3,
            _ => 3,
        }
    }

    pub fn pool_count(&self) -> usize {
        self.pool_count
    }

    pub fn verify_crc(&self) -> bool {
        if self.data.len() < 20 {
            return false;
        }
        let stored = read_u32_le(&self.data, 16);
        let mut tmp = self.data[..].to_vec();
        tmp[16..20].copy_from_slice(&[0u8; 4]);
        let computed = crc32_compute(&tmp);
        stored == computed
    }
}

fn fast_parse_ip_v4(ip: &str) -> Option<u32> {
    let bytes = ip.as_bytes();
    if bytes.is_empty() {
        return None;
    }
    let mut val = 0u32;
    let mut result = 0u32;
    let mut parts = 0u32;
    for (i, &b) in bytes.iter().enumerate() {
        if b >= b'0' && b <= b'9' {
            val = val * 10 + (b - b'0') as u32;
            if val > 255 {
                return None;
            }
        } else if b == b'.' {
            if i == 0 || bytes[i - 1] == b'.' {
                return None;
            }
            result = (result << 8) | val;
            val = 0;
            parts += 1;
        } else {
            return None;
        }
    }
    if parts != 3 {
        return None;
    }
    Some((result << 8) | val)
}
