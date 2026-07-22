use memmap2::Mmap;
use std::fs;

use std::sync::{Arc, OnceLock};

const MAX_TRIE_WALK_STEPS: usize = 1000;

#[derive(Debug)]
pub enum QzdbError {
    NotFound,
    Corrupted,
    OutOfBounds {
        offset: u64,
        required: u64,
        field: &'static str,
    },
    InvalidParam(&'static str),
    BadHeader(String),
    BadMagic,
    Unsupported(String),
    IoError(std::io::Error),
}

impl std::fmt::Display for QzdbError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            QzdbError::NotFound => write!(f, "NotFound"),
            QzdbError::Corrupted => write!(f, "Corrupted"),
            QzdbError::OutOfBounds { offset, required, field } => {
                write!(f, "OutOfBounds at offset {} (required {} bytes for field: {}), data may be truncated or corrupted", offset, required, field)
            },
            QzdbError::InvalidParam(msg) => write!(f, "InvalidParam: {}", msg),
            QzdbError::BadHeader(msg) => write!(f, "BadHeader: {}", msg),
            QzdbError::BadMagic => write!(f, "BadMagic"),
            QzdbError::Unsupported(msg) => write!(f, "Unsupported: {}", msg),
            QzdbError::IoError(e) => write!(f, "IoError: {}", e),
        }
    }
}

impl std::error::Error for QzdbError {}

impl From<std::io::Error> for QzdbError {
    fn from(e: std::io::Error) -> Self {
        QzdbError::IoError(e)
    }
}

const SENTINEL: u32 = 0x80000000;
const SENTINEL_MASK_24: u32 = 0x7FFFFF;
const SENTINEL_MASK_31: u32 = 0x7FFFFFFF;

static INSTANCE: OnceLock<QzdbSearcher> = OnceLock::new();

static CRC32_TABLE: OnceLock<[u32; 256]> = OnceLock::new();

fn crc32_compute(data: &[u8]) -> u32 {
    let table = crc32_table();
    let mut crc: u32 = 0xFFFFFFFF;
    for &b in data {
        crc = table[((crc ^ b as u32) & 0xFF) as usize] ^ (crc >> 8);
    }
    crc ^ 0xFFFFFFFF
}

fn crc32_table() -> &'static [u32; 256] {
    CRC32_TABLE.get_or_init(|| {
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
    })
}

#[derive(Debug, Clone)]
pub struct GeoInfo {
    pub values: Vec<String>,
    pub field_names: Arc<Vec<String>>,
    pub float_field_indices: Arc<Vec<usize>>,
}

impl GeoInfo {
    pub fn get(&self, name: &str) -> &str {
        self.field_names
            .iter()
            .position(|n| n == name)
            .and_then(|i| self.values.get(i))
            .map(|s| s.as_str())
            .unwrap_or("")
    }

    pub fn to_pipe(&self) -> String {
        let mut parts = Vec::with_capacity(self.field_names.len());
        for (i, _) in self.field_names.iter().enumerate() {
            let val = self.values.get(i).cloned().unwrap_or_default();
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
    field_names: Arc<Vec<String>>,
    field_name_to_idx: Arc<std::collections::HashMap<String, usize>>,
    float_field_indices: Arc<Vec<usize>>,
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

/// Safe read helpers — return None on out-of-bounds instead of panicking.
/// Used in hot paths (trie walk, pool/geo access) for SEC-01+SEC-02 hardening.

#[inline(always)]
fn safe_read_u16(data: &[u8], off: usize) -> Option<u16> {
    if off + 2 > data.len() { return None; }
    Some(u16::from_le_bytes(data[off..off + 2].try_into().ok()?))
}

#[inline(always)]
fn safe_read_u32(data: &[u8], off: usize) -> Option<u32> {
    if off + 4 > data.len() { return None; }
    Some(u32::from_le_bytes(data[off..off + 4].try_into().ok()?))
}

#[inline(always)]
fn safe_read_u64(data: &[u8], off: usize) -> Option<u64> {
    if off + 8 > data.len() { return None; }
    Some(u64::from_le_bytes(data[off..off + 8].try_into().ok()?))
}

#[inline(always)]
fn safe_read_u24(data: &[u8], off: usize) -> Option<u32> {
    if off + 3 > data.len() { return None; }
    Some(data[off] as u32 | (data[off + 1] as u32) << 8 | (data[off + 2] as u32) << 16)
}

#[inline(always)]
fn safe_read_u48(data: &[u8], off: usize) -> Option<u64> {
    if off + 6 > data.len() { return None; }
    Some(data[off] as u64
        | (data[off + 1] as u64) << 8
        | (data[off + 2] as u64) << 16
        | (data[off + 3] as u64) << 24
        | (data[off + 4] as u64) << 32
        | (data[off + 5] as u64) << 40)
}

#[inline(always)]
fn safe_read_uint_width(data: &[u8], off: usize, width: usize) -> Option<u32> {
    if width <= 1 {
        if off >= data.len() { return None; }
        Some(data[off] as u32)
    } else if width == 2 {
        safe_read_u16(data, off).map(|v| v as u32)
    } else if width == 3 {
        safe_read_u24(data, off)
    } else {
        safe_read_u32(data, off)
    }
}

/// Legacy unsafe read helpers — used ONLY in header parsing where
/// check_offset() already guarantees all offsets are within bounds.
#[inline(always)]
unsafe fn read_u16_le_unchecked(data: &[u8], off: usize) -> u16 {
    u16::from_le_bytes(data[off..off + 2].try_into().unwrap_unchecked())
}

#[inline(always)]
unsafe fn read_u32_le_unchecked(data: &[u8], off: usize) -> u32 {
    u32::from_le_bytes(data[off..off + 4].try_into().unwrap_unchecked())
}

#[inline(always)]
unsafe fn read_u64_le_unchecked(data: &[u8], off: usize) -> u64 {
    u64::from_le_bytes(data[off..off + 8].try_into().unwrap_unchecked())
}

#[inline(always)]
unsafe fn read_u48_le_unchecked(data: &[u8], off: usize) -> u64 {
    data[off] as u64
        | (data[off + 1] as u64) << 8
        | (data[off + 2] as u64) << 16
        | (data[off + 3] as u64) << 24
        | (data[off + 4] as u64) << 32
        | (data[off + 5] as u64) << 40
}

pub fn instance(db_path: &str) -> Result<&'static QzdbSearcher, QzdbError> {
    Ok(INSTANCE.get_or_init(|| {
        from_file(db_path).expect("Failed to initialize database instance")
    }))
}

pub fn from_file(db_path: &str) -> Result<QzdbSearcher, QzdbError> {
    let file = fs::File::open(db_path).map_err(|_| QzdbError::BadMagic)?;
    let mmap = unsafe { Mmap::map(&file).map_err(|_| QzdbError::Corrupted)? };
    QzdbSearcher::new(mmap, 0)
}

impl QzdbSearcher {
    pub fn new(data: Mmap, group_index: usize) -> Result<Self, QzdbError> {
        let data_len = data.len() as u64;
        
        if data_len < 192 {
            return Err(QzdbError::Corrupted);
        }

        let magic = &data[..4];
        if magic != b"QZDB" {
            return Err(QzdbError::BadMagic);
        }

        let fmt_ver = data[4];
        if fmt_ver < 1 || fmt_ver > 6 {
            return Err(QzdbError::Unsupported(format!("Unsupported version: {}", fmt_ver)));
        }

        let flags = unsafe { read_u16_le_unchecked(&data, 8) };
        let has_v4 = flags & 1 != 0;
        let has_v6 = flags & 2 != 0;
        let v4_node_24 = flags & 0x10 != 0;
        let v6_node_24 = flags & 0x20 != 0;

        let mut v6_jump_bits = data[11] as usize;
        if v6_jump_bits == 0 {
            v6_jump_bits = 16;
        }
        if v6_jump_bits < 16 || v6_jump_bits > 20 {
            return Err(QzdbError::OutOfBounds { offset: v6_jump_bits as u64, required: 0, field: "v6_jump_bits must be 16..20" });
        }

        let pool_count = data[12] as usize;
        let pool_idx_size = data[13] as usize;
        if pool_idx_size != 2 && pool_idx_size != 3 {
            return Err(QzdbError::OutOfBounds { offset: pool_idx_size as u64, required: 0, field: "pool_idx_size must be 2 or 3" });
        }
        let geo_count = unsafe { read_u16_le_unchecked(&data, 14) } as usize;
        let row_count = unsafe { read_u32_le_unchecked(&data, 20) } as usize;
        let v4_rec_count = unsafe { read_u32_le_unchecked(&data, 24) };
        let v6_rec_count = unsafe { read_u32_le_unchecked(&data, 28) };

        let hs = unsafe { read_u32_le_unchecked(&data, 36) };
        if hs != 192 {
            return Err(QzdbError::BadHeader(format!("Unexpected header size: {}", hs)));
        }

        let off_row_schema = unsafe { read_u64_le_unchecked(&data, 40) };
        let off_group_schema = unsafe { read_u64_le_unchecked(&data, 48) };
        let off_v4_jump = unsafe { read_u64_le_unchecked(&data, 64) };
        let off_v4_nodes = unsafe { read_u64_le_unchecked(&data, 72) };
        let off_v6_jump = unsafe { read_u64_le_unchecked(&data, 80) };
        let off_v6_nodes = unsafe { read_u64_le_unchecked(&data, 88) };
        let off_ip_row = unsafe { read_u64_le_unchecked(&data, 96) };
        let off_geo_entries = unsafe { read_u64_le_unchecked(&data, 104) };
        let off_pools = unsafe { read_u64_le_unchecked(&data, 136) };
        let off_meta = unsafe { read_u64_le_unchecked(&data, 144) };

        let v4_node_count = unsafe { read_u32_le_unchecked(&data, 152) };
        let v6_node_count = unsafe { read_u32_le_unchecked(&data, 156) };
        let ip_row_size = unsafe { read_u32_le_unchecked(&data, 160) } as usize;
        if ip_row_size < 1 || ip_row_size > 64 {
            return Err(QzdbError::OutOfBounds { offset: ip_row_size as u64, required: 0, field: "ip_row_size out of range [1,64]" });
        }
        let geo_entry_group_count = unsafe { read_u32_le_unchecked(&data, 164) } as usize;
        if geo_entry_group_count < 1 || geo_entry_group_count > 255 {
            return Err(QzdbError::OutOfBounds { offset: geo_entry_group_count as u64, required: 0, field: "geo_entry_group_count out of range [1,255]" });
        }

        // Validate offsets are within bounds (overflow-safe arithmetic)
        fn check_offset(data_len: u64, offset: u64, required: u64, field: &'static str) -> Result<(), QzdbError> {
            let end = match offset.checked_add(required) {
                Some(end) => end,
                None => {
                    return Err(QzdbError::OutOfBounds {
                        offset,
                        required,
                        field,
                    })
                }
            };
            if end > data_len {
                return Err(QzdbError::OutOfBounds {
                    offset,
                    required,
                    field,
                });
            }
            Ok(())
        }

        let v4_node_size = if v4_node_24 { 6u64 } else { 8u64 };
        let v6_node_size = if v6_node_24 { 6u64 } else { 8u64 };
        let v6_jump_size = (1u64 << v6_jump_bits) * 4;

        check_offset(data_len, off_v4_jump, 65536 * 4, "off_v4_jump")?;
        check_offset(data_len, off_v4_nodes, v4_node_count as u64 * v4_node_size, "off_v4_nodes")?;
        check_offset(data_len, off_v6_jump, v6_jump_size, "off_v6_jump")?;
        check_offset(data_len, off_v6_nodes, v6_node_count as u64 * v6_node_size, "off_v6_nodes")?;
        check_offset(data_len, off_ip_row, row_count as u64 * ip_row_size as u64, "off_ip_row")?;
        check_offset(data_len, off_geo_entries, 16, "off_geo_entries")?;
        check_offset(data_len, off_pools, 4, "off_pools")?;
        check_offset(data_len, off_meta, 4, "off_meta")?;
        if off_group_schema > 0 {
            check_offset(data_len, off_group_schema, 2, "off_group_schema")?;
        }
        if off_row_schema > 0 {
            check_offset(data_len, off_row_schema, 1, "off_row_schema")?;
        }

        let mut group_entry_offsets = Vec::with_capacity(4);
        for i in 0..4 {
            group_entry_offsets.push(unsafe { read_u48_le_unchecked(&data, 168 + i * 6) });
        }

        let mut gm_off = off_geo_entries;
        let group_count = data[gm_off as usize] as usize;
        gm_off += 1;

        let mut actual_groups = group_count.min(1.max(geo_entry_group_count));
        if actual_groups > 4 {
            actual_groups = 4;
        }
        let mut group_field_counts = vec![0; actual_groups];
        let mut group_entry_counts = vec![0; actual_groups];
        let mut group_dim_masks = vec![0; actual_groups];

        for gi in 0..actual_groups {
            group_field_counts[gi] = data[gm_off as usize] as usize;
            gm_off += 1;
            if fmt_ver == 1 || fmt_ver >= 4 {
                group_entry_counts[gi] = unsafe { read_u32_le_unchecked(&data, gm_off as usize) };
                gm_off += 4;
            } else {
                group_entry_counts[gi] = unsafe { read_u16_le_unchecked(&data, gm_off as usize) } as u32;
                gm_off += 2;
            }

            if fmt_ver == 1 || fmt_ver >= 3 {
                group_dim_masks[gi] = unsafe { read_u16_le_unchecked(&data, gm_off as usize) };
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
            let gs_group_count = unsafe { read_u16_le_unchecked(&data, sp) } as usize;
            sp += 2;
            let max_gs_groups = gs_group_count.min(actual_groups);
            for gi in 0..max_gs_groups {
                sp += 2; // skip groupId
                let fld_count = unsafe { read_u16_le_unchecked(&data, sp) } as usize;
                sp += 2;
                sp += 4; // skip entryCount
                let stride = unsafe { read_u32_le_unchecked(&data, sp) } as usize;
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
                        offsets[fi] = unsafe { read_u32_le_unchecked(&data, sp) } as usize;
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
            field_names: Arc::new(Vec::new()),
            field_name_to_idx: Arc::new(std::collections::HashMap::new()),
            float_field_indices: Arc::new(Vec::new()),
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
        Ok(s)
    }

    fn resolve_field_names(&mut self) {
        let d = &self.data;
        let off_meta = self.off_meta;
        if (self.flags & 4) != 0 && off_meta > 0 && off_meta + 4 <= d.len() as u64 {
            let mut field_names: Option<Vec<String>> = None;
            let mut pos = off_meta as usize;
            while pos + 4 <= d.len() {
                let t = d[pos];
                let length = match safe_read_u16(d, pos + 2) {
                    Some(v) => v as usize,
                    None => break,
                };
                if t == 0 || length == 0 {
                    break;
                }
                if pos + 4 + length > d.len() {
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
                    self.field_names = Arc::new(names);
                    self.field_name_to_idx = Arc::new(self.field_names.iter().enumerate()
                        .map(|(i, n)| (n.clone(), i)).collect());
                    self.float_field_indices = Arc::new(self.field_names.iter().enumerate()
                        .filter(|(_, n)| *n == "longitude" || *n == "latitude")
                        .map(|(i, _)| i).collect());
                    return;
                }
            }
        }

        self.field_names = Arc::new((0..self.group_field_counts[0]).map(|i| format!("field_{}", i)).collect());
        self.field_name_to_idx = Arc::new(self.field_names.iter().enumerate()
            .map(|(i, n)| (n.clone(), i)).collect());
        self.float_field_indices = Arc::new(Vec::new());
    }

    fn ensure_pools_loaded(&self) -> &Vec<Vec<Vec<String>>> {
        self.group_pools.get_or_init(|| {
            let group_count = self.group_field_counts.len();
            let mut group_pools = vec![Vec::new(); group_count];
            if self.off_pools == 0 {
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
                    let count = match safe_read_u32(d, pool_cursor) {
                        Some(v) => v as usize,
                        None => {
                            group_pool_list.push(Vec::new());
                            continue;
                        }
                    };
                    pool_cursor += 4;
                    if self.off_row_schema > 0 {
                        pool_cursor += 4;
                    }
                    if count == 0 || count > 16_000_000 {
                        group_pool_list.push(Vec::new());
                        continue;
                    }

                    let mut offsets = Vec::with_capacity(count + 1);
                    for _ in 0..=count {
                        if pool_cursor + 4 > pool_end {
                            break;
                        }
                        match safe_read_u32(d, pool_cursor) {
                            Some(v) => offsets.push(v as usize),
                            None => break,
                        }
                        pool_cursor += 4;
                    }
                    if offsets.len() <= count {
                        // Truncated pool table: skip this pool rather than panic.
                        group_pool_list.push(Vec::new());
                        continue;
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
        safe_read_uint_width(&self.data, off, width).unwrap_or(0)
    }

    fn get_v4_child(&self, node_idx: u32, bit: u32) -> u32 {
        if node_idx >= self.v4_node_count {
            return 0;
        }
        if self.v4_node_24 {
            let node_offset = self.off_v4_nodes as usize + node_idx as usize * 6;
            let offset = if bit == 0 { node_offset } else { node_offset + 3 };
            let val = match safe_read_u24(&self.data, offset) {
                Some(v) => v,
                None => return 0,
            };
            if val & 0x800000 != 0 {
                return (val & SENTINEL_MASK_24) | SENTINEL;
            }
            val
        } else {
            let child_off = self.off_v4_nodes as usize + node_idx as usize * 8 + bit as usize * 4;
            safe_read_u32(&self.data, child_off).unwrap_or(0)
        }
    }

    fn get_v6_child(&self, node_idx: u32, bit: u32) -> u32 {
        if node_idx >= self.v6_node_count {
            return 0;
        }
        if self.v6_node_24 {
            let node_offset = self.off_v6_nodes as usize + node_idx as usize * 6;
            let offset = if bit == 0 { node_offset } else { node_offset + 3 };
            let val = match safe_read_u24(&self.data, offset) {
                Some(v) => v,
                None => return 0,
            };
            if val & 0x800000 != 0 {
                return (val & SENTINEL_MASK_24) | SENTINEL;
            }
            val
        } else {
            let child_off = self.off_v6_nodes as usize + node_idx as usize * 8 + bit as usize * 4;
            safe_read_u32(&self.data, child_off).unwrap_or(0)
        }
    }

    fn trie_walk_v4(&self, ip_int: u32) -> u32 {
        let hi16 = ((ip_int >> 16) & 0xFFFF) as usize;
        let ptr = match safe_read_u32(&self.data, (self.off_v4_jump as usize) + hi16 * 4) {
            Some(v) => v,
            None => return 0,
        };

        if ptr == 0 {
            return 0;
        }
        if ptr & SENTINEL != 0 {
            return ptr & SENTINEL_MASK_31;
        }

        let mut idx = ptr;
        let mut suffix = (ip_int & 0xFFFF) << 16;
        let mut steps = 0;

        loop {
            steps += 1;
            if steps >= MAX_TRIE_WALK_STEPS {
                return 0;
            }
            let bit = (suffix >> 31) & 1;
            let child = self.get_v4_child(idx, bit);

            if child == 0 {
                return 0;
            }
            if child & SENTINEL != 0 {
                return child & SENTINEL_MASK_31;
            }

            idx = child;
            suffix <<= 1;
        }
    }

    fn trie_walk_v6(&self, ip_int: u128) -> u32 {
        let shift = 128 - self.v6_jump_bits;
        let idx_jump = ((ip_int >> shift) & ((1 << self.v6_jump_bits) - 1)) as usize;
        let ptr = match safe_read_u32(&self.data, (self.off_v6_jump as usize) + idx_jump * 4) {
            Some(v) => v,
            None => return 0,
        };

        if ptr == 0 {
            return 0;
        }
        if ptr & SENTINEL != 0 {
            return ptr & SENTINEL_MASK_31;
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
                return child & SENTINEL_MASK_31;
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
        let geo_id = safe_read_u24(&self.data, off).unwrap_or(0);
        let asn_id = safe_read_u24(&self.data, off + 3).unwrap_or(0);

        let mut usage_type_id = 0;
        if self.ip_row_size >= 9 {
            usage_type_id = safe_read_u24(&self.data, off + 6).unwrap_or(0);
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

        let mut values = Vec::with_capacity(field_count);
        for i in 0..field_count {
            let w = widths[i];
            let fo = entry_offset + base_offsets[i];
            let is_native = natives[i];

            let val = if is_native {
                let t = nat_types[i];
                if t == 1 {
                    if w == 4 {
                        let bits = safe_read_u32(d, fo).unwrap_or(0);
                        f32::from_bits(bits).to_string()
                    } else {
                        let bits = safe_read_u64(d, fo).unwrap_or(0);
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

            values.push(val);
        }

        Some(GeoInfo {
            values,
            field_names: self.field_names.clone(),
            float_field_indices: self.float_field_indices.clone(),
        })
    }

    pub fn find(&self, ip_str: &str) -> Option<GeoInfo> {
        if ip_str.is_empty() { return None; }
        match fast_parse_ip(ip_str)? {
            ParseIpResult::V4(v4) => self.find_uint(v4),
            ParseIpResult::V6(v6) => self.find_v6(v6),
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

    pub fn lookup_row_id(&self, ip_str: &str) -> u32 {
        if ip_str.is_empty() { return 0; }
        match fast_parse_ip(ip_str) {
            Some(ParseIpResult::V4(v4)) => self.lookup_row_id_uint(v4),
            Some(ParseIpResult::V6(v6)) => self.lookup_row_id_v6(v6),
            None => 0,
        }
    }

    pub fn lookup_row_id_uint(&self, ip_int: u32) -> u32 {
        if !self.has_v4 { return 0; }
        self.trie_walk_v4(ip_int)
    }

    pub fn lookup_row_id_v6(&self, ip_int: u128) -> u32 {
        if !self.has_v6 { return 0; }
        self.trie_walk_v6(ip_int)
    }

    pub fn lookup_ids(&self, row_id: u32) -> Option<(u32, u32, u32)> {
        if row_id == 0 || row_id >= self.row_count as u32 { return None; }
        let r = self.read_ip_row(row_id);
        Some((r.0, r.1, r.2))
    }

    pub fn find_fields(&self, ip_str: &str, field_names: &[&str]) -> Option<GeoInfo> {
        if field_names.is_empty() {
            return self.find(ip_str);
        }
        let row_id = self.lookup_row_id(ip_str);
        if row_id == 0 {
            return None;
        }
        self.resolve_geo_fields(row_id, self.group_index, field_names)
    }

    fn resolve_geo_fields(&self, row_id: u32, group_index: usize, field_names: &[&str]) -> Option<GeoInfo> {
        let (geo_id, asn_id, usage_type_id) = self.read_ip_row(row_id);
        let mask = self.group_dim_masks.get(group_index).copied().unwrap_or(0);
        let entry_id = if mask & 0x02 != 0 { asn_id } else if mask & 0x04 != 0 { usage_type_id } else { geo_id };
        if entry_id == 0 || group_index >= self.group_field_counts.len() {
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
        // Use cached name→index map
        let indices: Vec<usize> = field_names.iter()
            .filter_map(|name| self.field_name_to_idx.get(*name).copied()).collect();
        if indices.is_empty() {
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

        let mut values = vec![String::new(); field_count];
        for &i in &indices {
            if i >= field_count { continue; }
            let w = widths[i];
            let fo = entry_offset + base_offsets[i];
            values[i] = if natives[i] {
                let t = nat_types[i];
                if t == 1 {
                    if w == 4 { f32::from_bits(safe_read_u32(d, fo).unwrap_or(0)).to_string() }
                    else { f64::from_bits(safe_read_u64(d, fo).unwrap_or(0)).to_string() }
                } else {
                    self.read_uint_width(fo, w).to_string()
                }
            } else {
                let idx = self.read_uint_width(fo, w) as usize;
                let gp = &pools[group_index];
                if i < gp.len() && idx < gp[i].len() { gp[i][idx].clone() }
                else { String::new() }
            };
        }
        Some(GeoInfo { values, field_names: self.field_names.clone(), float_field_indices: self.float_field_indices.clone() })
    }

    /// Reload database from a new file path, replacing current state.
    pub fn reload(&mut self, path: &str) -> Result<(), QzdbError> {
        let file = fs::File::open(path).map_err(|_| QzdbError::BadMagic)?;
        let mmap = unsafe { Mmap::map(&file).map_err(|_| QzdbError::Corrupted)? };
        *self = Self::new(mmap, self.group_index)?;
        Ok(())
    }

    pub fn find_str(&self, ip_str: &str) -> String {
        self.find(ip_str).map(|info| info.to_pipe()).unwrap_or_default()
    }

    pub fn field_names(&self) -> &[String] {
        self.field_names.as_slice()
    }

    pub fn float_indices(&self) -> &[usize] {
        self.float_field_indices.as_slice()
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
        let stored = safe_read_u32(&self.data, 16).unwrap_or(0);
        let table = crc32_table();
        let mut crc = 0xFFFFFFFFu32;
        for &b in &self.data[..16] {
            crc = table[((crc ^ b as u32) & 0xFF) as usize] ^ (crc >> 8);
        }
        // Skip bytes 16..20 (CRC slot, treated as zero)
        for &b in &self.data[20..] {
            crc = table[((crc ^ b as u32) & 0xFF) as usize] ^ (crc >> 8);
        }
        stored == (crc ^ 0xFFFFFFFF)
    }
}

enum ParseIpResult {
    V4(u32),
    V6(u128),
}

static HEX: [u8; 128] = {
    let mut h = [0u8; 128];
    let mut i = 0u8;
    while i < 10 { h[48 + i as usize] = i; i += 1; }
    let mut i = 0u8;
    while i < 6 { h[97 + i as usize] = 10 + i; h[65 + i as usize] = 10 + i; i += 1; }
    h
};

fn fast_parse_ip_v4(s: &str) -> Option<u32> {
    let bytes = s.as_bytes();
    let n = bytes.len();
    if n == 0 || bytes[n - 1] == b'.' { return None; }
    let mut result = 0u32;
    let mut dots = 0u32;
    let mut start = 0;
    for i in 0..=n {
        let c = if i < n { bytes[i] } else { b'.' };
        if c == b'.' {
            let seg_len = i - start;
            if seg_len == 0 || seg_len > 3 { return None; }
            if seg_len > 1 && bytes[start] == b'0' { return None; }
            let mut val = 0u32;
            for j in start..i {
                let d = bytes[j];
                if d < b'0' || d > b'9' { return None; }
                val = val * 10 + (d - b'0') as u32;
            }
            if val > 255 { return None; }
            result = (result << 8) | val;
            dots += 1;
            start = i + 1;
        }
    }
    if dots != 4 { return None; }
    Some(result)
}

fn fast_parse_ip(s: &str) -> Option<ParseIpResult> {
    let n = s.len();
    // Reject whitespace — SSRF-safe, cross-language consistent
    for &b in s.as_bytes() {
        if b == b' ' || b == b'\t' || b == b'\n' || b == b'\r' || b == 0x0B || b == 0x0C {
            return None;
        }
    }
    if n == 0 || n > 45 { return None; }
    if !s.contains(':') {
        return fast_parse_ip_v4(s).map(ParseIpResult::V4);
    }
    if s.contains('%') { return None; }
    let dc = s.find("::");
    if let Some(dc) = dc {
        if s[dc + 2..].find("::").is_some() { return None; }
    }
    let (lft, rgt) = match dc {
        Some(dc) => (&s[..dc], &s[dc + 2..]),
        None => (s, ""),
    };
    let mut lg: Vec<&str> = if lft.is_empty() { Vec::new() } else { lft.split(':').collect() };
    let mut rg: Vec<&str> = if rgt.is_empty() { Vec::new() } else { rgt.split(':').collect() };
    if lg.len() == 1 && lg[0] == "" { lg.clear(); }
    if rg.len() == 1 && rg[0] == "" { rg.clear(); }
    for g in &lg { if g.is_empty() { return None; } }
    for g in &rg { if g.is_empty() { return None; } }
    let mut allg: Vec<&str> = Vec::with_capacity(lg.len() + rg.len());
    allg.extend_from_slice(&lg);
    allg.extend_from_slice(&rg);
    let mut has_v4 = false;
    let mut v4_int = 0u32;
    if let Some(last) = allg.last() {
        if last.contains('.') {
            v4_int = fast_parse_ip_v4(last)?;
            has_v4 = true;
            allg.pop();
        }
    }
    let ng = allg.len();
    let v4_slots: usize = if has_v4 { 2 } else { 0 };
    if dc.is_some() {
        if ng + v4_slots > 7 { return None; }
    } else {
        if ng + v4_slots != 8 { return None; }
    }
    for g in &allg {
        let gl = g.len();
        if gl == 0 || gl > 4 { return None; }
        for &cc in g.as_bytes() {
            if cc >= 128 || (HEX[cc as usize] == 0 && cc != b'0') { return None; }
        }
    }
    let zeros = 8 - ng - v4_slots;
    let mut buf = [0u8; 16];
    let mut off = 0usize;
    for g in &lg {
        let mut v = 0u16;
        for &c in g.as_bytes() { v = (v << 4) | HEX[c as usize] as u16; }
        buf[off] = (v >> 8) as u8;
        buf[off + 1] = v as u8;
        off += 2;
    }
    off += zeros * 2;
    for g in &rg {
        let mut v = 0u16;
        for &c in g.as_bytes() { v = (v << 4) | HEX[c as usize] as u16; }
        buf[off] = (v >> 8) as u8;
        buf[off + 1] = v as u8;
        off += 2;
    }
    if has_v4 {
        buf[12] = (v4_int >> 24) as u8;
        buf[13] = (v4_int >> 16) as u8;
        buf[14] = (v4_int >> 8) as u8;
        buf[15] = v4_int as u8;
    }
    if buf[10] == 0xff && buf[11] == 0xff
        && buf[..10].iter().all(|&b| b == 0)
    {
        return Some(ParseIpResult::V4(u32::from_be_bytes([buf[12], buf[13], buf[14], buf[15]])));
    }
    let mut v128 = 0u128;
    for &b in &buf {
        v128 = (v128 << 8) | b as u128;
    }
    Some(ParseIpResult::V6(v128))
}
