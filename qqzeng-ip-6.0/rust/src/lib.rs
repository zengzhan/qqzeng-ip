use std::fs;
use std::path::PathBuf;
use std::sync::OnceLock;
use std::str;
use std::io::{Error, ErrorKind};

// 常量定义
const INDEX_START_INDEX: usize = 0x30004;
const END_MASK: u32 = 0x800000;
const COMPL_MASK: u32 = !END_MASK;
const DB_FILE_NAME: &str = "qqzeng-ip-6.0-global.db";

pub struct IpDbSearch {
    data: Vec<u8>,
    // 为了极致性能且保持Safe Rust，我们这里存储 String (拥有所有权)
    // 这样避免了复杂的自引用生命周期问题
    geoisp_arr: Vec<String>,
}

// 单例存储
static INSTANCE: OnceLock<IpDbSearch> = OnceLock::new();

impl IpDbSearch {
    /// 获取单例实例
    pub fn instance() -> &'static IpDbSearch {
        INSTANCE.get_or_init(|| {
            IpDbSearch::new().expect("Failed to initialize IpDbSearch")
        })
    }

    /// 初始化
    fn new() -> Result<Self, Error> {
        let db_path = Self::find_db_path().ok_or_else(|| {
            Error::new(ErrorKind::NotFound, format!("Fatal: Cannot find {}", DB_FILE_NAME))
        })?;

        let data = fs::read(db_path)?;

        if data.len() < INDEX_START_INDEX {
            return Err(Error::new(ErrorKind::InvalidData, "Invalid database file size"));
        }

        // 读取节点数量 (手动解析小端序)
        let node_count = u32::from_le_bytes([data[0], data[1], data[2], data[3]]) as usize;
        
        let string_area_offset = INDEX_START_INDEX + node_count * 6;
        
        if string_area_offset > data.len() {
            return Err(Error::new(ErrorKind::InvalidData, "Invalid metadata"));
        }

        // 解析字符串区 (一次性分配)
        let content_bytes = &data[string_area_offset..];
        // 校验UTF-8
        let content = str::from_utf8(content_bytes)
            .map_err(|e| Error::new(ErrorKind::InvalidData, e))?;
            
        // 预分配数组
        let geoisp_arr: Vec<String> = content.split('\t').map(|s| s.to_string()).collect();

        Ok(IpDbSearch {
            data,
            geoisp_arr,
        })
    }

    /// 查找IP
    #[inline]
    pub fn find(&self, ip: &str) -> &str {
        if ip.is_empty() {
            return "";
        }
        
        match self.fast_parse_ip(ip) {
            Ok((prefix, suffix)) => self.find_uint(prefix, suffix),
            Err(_) => "",
        }
    }

    /// 查找IP (已解析的整数)
    #[inline]
    pub fn find_uint(&self, prefix: u16, suffix: u16) -> &str {
        let prefix_idx = prefix as usize;
        
        // 一级索引
        // 4 + prefix * 3
        let mut record = self.read_int24(4 + prefix_idx * 3);

        let mut current_suffix = suffix;

        // 二叉树遍历
        while (record & END_MASK) != END_MASK {
            let bit = (current_suffix >> 15) & 1;
            // 节点区偏移
            let offset = INDEX_START_INDEX + (record as usize) * 6 + (bit as usize) * 3;
            record = self.read_int24(offset);
            current_suffix <<= 1;
        }

        let index = (record & COMPL_MASK) as usize;
        if index < self.geoisp_arr.len() {
            return &self.geoisp_arr[index];
        }
        ""
    }

    #[inline(always)]
    fn read_int24(&self, offset: usize) -> u32 {
        // Rust bounds check removal depends on optimizer
        // 安全起见我们使用 get 或者直接索引 (会panic如果越界，但在逻辑保证下是安全的)
        // 大端序 High Mid Low
        let b0 = self.data[offset] as u32;
        let b1 = self.data[offset + 1] as u32;
        let b2 = self.data[offset + 2] as u32;
        (b0 << 16) | (b1 << 8) | b2
    }

    #[inline(always)]
    fn fast_parse_ip(&self, ip: &str) -> Result<(u16, u16), ()> {
        let mut val: u32 = 0;
        let mut result: u32 = 0;
        let mut shift = 24;
        
        for b in ip.bytes() {
            if b >= b'0' && b <= b'9' {
                val = val * 10 + (b - b'0') as u32;
            } else if b == b'.' {
                if val > 255 { return Err(()); }
                result |= val << shift;
                val = 0;
                if shift == 0 { break; } // Should not happen for valid IP
                shift -= 8;
            } else {
                return Err(());
            }
        }
        
        if val > 255 || shift != 0 { return Err(()); }
        result |= val;

        Ok(((result >> 16) as u16, (result & 0xFFFF) as u16))
    }

    fn find_db_path() -> Option<PathBuf> {
        let current_exe = std::env::current_exe().ok()?;
        let current_dir = current_exe.parent()?;
        
        let attempts = [
            current_dir.join(DB_FILE_NAME),
            current_dir.join("../data").join(DB_FILE_NAME),
            current_dir.join("../../data").join(DB_FILE_NAME),
            current_dir.join("../../../data").join(DB_FILE_NAME),
            PathBuf::from(format!("../data/{}", DB_FILE_NAME)), 
            PathBuf::from(DB_FILE_NAME),
        ];

        for path in &attempts {
            if path.exists() {
                return Some(path.clone());
            }
        }
        None
    }
}
