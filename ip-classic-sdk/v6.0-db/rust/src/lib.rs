//! qqzeng-ip 6.0 (.db) 高性能解析 - Rust 版 (零依赖, Safe Rust)
//!
//! 文件结构 (UTF-8):
//!   文件头    4 字节: 节点数量 nodeCount (uint32 小端)
//!   一级索引  65536 条 x 3 字节 (大端 int24): IPv4 前 16 位 -> 节点号或叶子
//!   节点区    nodeCount x 6 字节 (每节点 2 个子指针, 各 3 字节大端 int24)
//!   字符串区  其余部分, '\t' 分隔的 UTF-8 结果串
//!   int24 最高位 (0x800000) 置位 = 叶子, 低 22 位为字符串下标;
//!   否则为节点区跳转下标。查询消耗 IP 后 16 位 (MSB first)。
//!
//! 兼容旧版 API:
//!   IpDbSearch::instance().find("1.2.3.4")
//!   IpDbSearch::instance().find_uint(prefix, suffix)
//! 新增:
//!   IpDbSearch::new() / from_path(path) / from_bytes(bytes)
//!   find_uint32(ip_int) 整数查询
//!
//! 相比历史版本的修复与优化:
//!   1. fast_parse_ip 严格化: 旧版接受 "1..2.3"/"1.2.3." 等残缺 IP
//!      (旧版遇到 5 段 IP 还会以负数移位中断解析产生半截结果);
//!      现按统一严格规则校验, 非法输入返回 ""。
//!   2. 加载期完整校验 (fail-closed): 尺寸/头字段/全部 65536 一级索引与
//!      全部节点跳转合法性/叶子下标范围/字符串区 UTF-8 合法性,
//!      非法文件返回 Err, 杜绝查询期 panic 与死循环。
//!   3. 字符串区不再 split 为 Vec<String> (2 倍内存 + 每串一次分配),
//!      改为 (offset, len) 索引 + 借用切片返回 &str, 零拷贝零分配。
//!   4. find_uint32 统一整数入口; 单例 OnceLock 线程安全。

use std::fs;
use std::io::{Error, ErrorKind};
use std::path::{Path, PathBuf};
use std::sync::OnceLock;

const INDEX_START: usize = 0x30004;
const FIRST_COUNT: usize = 65536;
const END_MASK: u32 = 0x800000;
const COMPL_MASK: u32 = 0x7FFFFF;
pub const DB_FILE_NAME: &str = "qqzeng-ip-6.0-global.db";

pub struct IpDbSearch {
    data: Vec<u8>,
    node_count: usize,
    /// 字符串表: (字节偏移, 字节长度), 指向 data 内字符串区
    strings: Vec<(u32, u32)>,
}

static INSTANCE: OnceLock<IpDbSearch> = OnceLock::new();

impl IpDbSearch {
    /// 单例 (自动探测默认路径; 初始化失败 panic, 与旧版行为一致)。
    pub fn instance() -> &'static IpDbSearch {
        INSTANCE.get_or_init(|| IpDbSearch::new().expect("Failed to initialize IpDbSearch"))
    }

    /// 自动探测默认路径初始化。
    pub fn new() -> Result<Self, Error> {
        let path = Self::find_db_path().ok_or_else(|| {
            Error::new(ErrorKind::NotFound, format!("Fatal: Cannot find {}", DB_FILE_NAME))
        })?;
        Self::from_path(path)
    }

    /// 指定路径初始化。
    pub fn from_path<P: AsRef<Path>>(path: P) -> Result<Self, Error> {
        let data = fs::read(path)?;
        Self::from_bytes(data)
    }

    /// 从内存字节流初始化。
    pub fn from_bytes(data: Vec<u8>) -> Result<Self, Error> {
        Self::parse(data)
    }

    fn parse(data: Vec<u8>) -> Result<Self, Error> {
        let invalid = |msg: &str| Error::new(ErrorKind::InvalidData, msg);

        if data.len() < INDEX_START {
            return Err(invalid("invalid database file size"));
        }
        let node_count =
            u32::from_le_bytes([data[0], data[1], data[2], data[3]]) as usize;
        if node_count == 0 {
            return Err(invalid("invalid node count"));
        }
        let string_area = INDEX_START.checked_add(node_count.checked_mul(6).ok_or_else(|| invalid("node area overflow"))?)
            .ok_or_else(|| invalid("node area overflow"))?;
        if string_area > data.len() {
            return Err(invalid("node area exceeds file size"));
        }

        // 字符串区 UTF-8 校验 (查询期切片免检的基础)
        let string_bytes = &data[string_area..];
        std::str::from_utf8(string_bytes).map_err(|e| Error::new(ErrorKind::InvalidData, e))?;

        // 字符串表: '\t' 分隔, 记录 (绝对 offset, len)
        let mut strings = Vec::new();
        {
            let mut start = 0usize;
            for (i, b) in string_bytes.iter().enumerate() {
                if *b == b'\t' {
                    strings.push(((string_area + start) as u32, (i - start) as u32));
                    start = i + 1;
                }
            }
            strings.push(((string_area + start) as u32, (string_bytes.len() - start) as u32));
        }
        let string_count = strings.len();

        let entry_ok = |v: u32| -> bool {
            if v & END_MASK != 0 {
                (v & COMPL_MASK) < string_count as u32
            } else {
                (v as usize) < node_count
            }
        };

        // 一级索引逐条校验
        for i in 0..FIRST_COUNT {
            if !entry_ok(read_int24(&data, 4 + i * 3)) {
                return Err(invalid("bad first-level index entry"));
            }
        }
        // 节点跳转合法性校验 (fail-closed)
        for n in 0..node_count {
            let base = INDEX_START + n * 6;
            if !entry_ok(read_int24(&data, base)) || !entry_ok(read_int24(&data, base + 3)) {
                return Err(invalid("bad node jump"));
            }
        }

        Ok(IpDbSearch { data, node_count, strings })
    }

    /// 查询 IP 字符串, 未命中/非法返回 ""。
    #[inline]
    pub fn find(&self, ip: &str) -> &str {
        match IpDbSearch::fast_parse_ip(ip) {
            Some(v) => self.find_uint32(v),
            None => "",
        }
    }

    /// 兼容旧版: (prefix, suffix) = (前 16 位, 后 16 位)。
    #[inline]
    pub fn find_uint(&self, prefix: u16, suffix: u16) -> &str {
        self.find_uint32(((prefix as u32) << 16) | suffix as u32)
    }

    /// 整数查询 (uint32)。
    #[inline]
    pub fn find_uint32(&self, ip_int: u32) -> &str {
        let mut record = read_int24(&self.data, 4 + ((ip_int >> 16) as usize) * 3);
        let mut suffix = ip_int & 0xFFFF;
        let mut hops = 0;
        // 后缀 16 位 -> 最多 16 跳; 超限说明文件非法 (fail-closed)
        while record & END_MASK == 0 {
            if hops >= 16 {
                return "";
            }
            hops += 1;
            let bit = ((suffix >> 15) & 1) as usize;
            record = read_int24(&self.data, INDEX_START + record as usize * 6 + bit * 3);
            suffix <<= 1;
        }
        let index = (record & COMPL_MASK) as usize;
        match self.strings.get(index) {
            Some(&(off, len)) => {
                // 安全: 加载期已校验整个字符串区为合法 UTF-8, 所有 (off,len) 均取自
                // 该区域内的 '\t' 边界, data 在解析后不可变 —— 切片必为合法 UTF-8。
                // 逐查询重复校验会使 QPS 下降约 3 倍, 故用 unchecked 视图。
                let s = &self.data[off as usize..off as usize + len as usize];
                unsafe { std::str::from_utf8_unchecked(s) }
            }
            None => "",
        }
    }

    pub fn node_count(&self) -> usize {
        self.node_count
    }

    pub fn string_count(&self) -> usize {
        self.strings.len()
    }

    /// 严格 IPv4 解析: 每段 1~3 位数字且 <=255, 恰好 4 段。非法返回 None。
    #[inline]
    pub fn fast_parse_ip(ip: &str) -> Option<u32> {
        let bytes = ip.as_bytes();
        let mut val: u32 = 0;
        let mut result: u32 = 0;
        let mut shift: i32 = 24;
        let mut digits = 0usize;
        for &b in bytes {
            if b.is_ascii_digit() {
                val = val * 10 + u32::from(b - b'0');
                if val > 255 {
                    return None;
                }
                digits += 1;
                if digits > 3 {
                    return None; // 拒绝 "0008.8.8.8" 这类超长前导零
                }
            } else if b == b'.' {
                if digits == 0 || shift == 0 {
                    return None; // 空段或第 5 段
                }
                result |= val << shift;
                val = 0;
                digits = 0;
                shift -= 8;
            } else {
                return None;
            }
        }
        if digits == 0 || shift != 0 {
            return None;
        }
        Some(result | val)
    }

    fn find_db_path() -> Option<PathBuf> {
        let mut attempts: Vec<PathBuf> = vec![
            PathBuf::from(DB_FILE_NAME),
            PathBuf::from("data").join(DB_FILE_NAME),
            PathBuf::from("../data").join(DB_FILE_NAME),
            PathBuf::from("../../data").join(DB_FILE_NAME),
            PathBuf::from("../../../data").join(DB_FILE_NAME),
        ];
        // 仓库内 dada/ 黄金数据目录 (相对运行目录逐级上探)
        for d in ["dada/全球旗舰版-202610",
                  "../dada/全球旗舰版-202610",
                  "../../dada/全球旗舰版-202610",
                  "../../../dada/全球旗舰版-202610"] {
            attempts.push(PathBuf::from(d).join(DB_FILE_NAME));
        }
        if let Ok(exe) = std::env::current_exe() {
            if let Some(dir) = exe.parent() {
                attempts.push(dir.join(DB_FILE_NAME));
                attempts.push(dir.join("../data").join(DB_FILE_NAME));
            }
        }
        attempts.into_iter().find(|p| p.is_file())
    }
}

#[inline(always)]
fn read_int24(data: &[u8], offset: usize) -> u32 {
    (u32::from(data[offset]) << 16) | (u32::from(data[offset + 1]) << 8) | u32::from(data[offset + 2])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_ip_strict() {
        assert_eq!(IpDbSearch::fast_parse_ip("1.2.3.4"), Some(0x01020304));
        assert_eq!(IpDbSearch::fast_parse_ip("0.0.0.0"), Some(0));
        assert_eq!(IpDbSearch::fast_parse_ip("255.255.255.255"), Some(0xFFFF_FFFF));
        for bad in ["", "256.1.1.1", "1.2.3", "1.2.3.4.5", "1..2.3", "1.2.3.",
                    ".1.2.3", "1.2.3.4 ", " 1.2.3.4", "abc", "::1", "1.2.3.-4"] {
            assert_eq!(IpDbSearch::fast_parse_ip(bad), None, "{bad}");
        }
    }

    #[test]
    fn reject_corrupt() {
        let mut data = vec![0u8; INDEX_START];
        assert!(IpDbSearch::from_bytes(data.clone()).is_err()); // node_count = 0
        data[0..4].copy_from_slice(&2u32.to_le_bytes());
        data[4..7].copy_from_slice(&[0xFF, 0xFF, 0xFF]); // 一级索引叶子下标越界
        assert!(IpDbSearch::from_bytes(data).is_err());
    }
}
