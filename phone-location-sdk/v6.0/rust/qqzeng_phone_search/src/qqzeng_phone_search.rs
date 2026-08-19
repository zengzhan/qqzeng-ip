use std::fs;
use std::io;
use std::path::Path;
use std::sync::OnceLock;
use byteorder::{LittleEndian, ReadBytesExt};
use std::borrow::Cow;

const HEADER_SIZE: usize = 32; // 8个uint的头部
const PREFIX_COUNT: usize = 200; // 电话号码前缀总数（0-199）
const BITMAP_POP_COUNT_OFFSET: usize = 0x4E2; // 位图统计信息偏移量

pub struct PhoneSearch6Db {
    data: Vec<u8>, // 数据库原始数据
    region_isps: Vec<Cow<'static, str>>, // 地区-运营商组合缓存 (使用 Cow 减少克隆)
    index: [(i32, i32); PREFIX_COUNT], // 前缀索引表
}

impl PhoneSearch6Db {
    /// 获取单例实例
    pub fn instance() -> &'static Self {
        static INSTANCE: OnceLock<PhoneSearch6Db> = OnceLock::new();
        INSTANCE.get_or_init(|| {
            let mut db = PhoneSearch6Db {
                data: Vec::new(),
                region_isps: Vec::new(),
                index: [(0, 0); PREFIX_COUNT],
            };
            db.load_database().expect("Failed to load database");
            db
        })
    }

    /// 加载并解析数据库文件
    fn load_database(&mut self) -> Result<(), Box<dyn std::error::Error>> {
        let file_path = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("qqzeng-phone-6.0.db")
            .to_str()
            .ok_or("Invalid file path")?
            .to_string();

        if !Path::new(&file_path).exists() {
            return Err(Box::new(io::Error::new(
                io::ErrorKind::NotFound,
                format!("Database file not found: {}", file_path),
            )));
        }

        self.data = fs::read(file_path)?;

        // 解析头部
        let mut header = [0u32; 8];
        for (i, value) in header.iter_mut().enumerate() {
            *value = (&self.data[i * 4..(i + 1) * 4]).read_u32::<LittleEndian>()?;
        }

        // 计算偏移量
        let regions_start = HEADER_SIZE;
        let isps_start = regions_start + header[1] as usize;
        let index_start = isps_start + header[2] as usize;

        // 解析地区和运营商表
        let regions = std::str::from_utf8(&self.data[regions_start..isps_start])?
            .split('&')
            .map(String::from)
            .collect::<Vec<_>>();
        let isps = std::str::from_utf8(&self.data[isps_start..index_start])?
            .split('&')
            .map(String::from)
            .collect::<Vec<_>>();

        // 构建地区-运营商组合
        self.region_isps = vec![];
        let entry_offset = header[3] as usize;
        for i in 0..header[4] as usize {
            let entry = (&self.data[entry_offset + i * 2..entry_offset + i * 2 + 2])
                .read_u16::<LittleEndian>()?;
            self.region_isps.push(Cow::Owned(format!(
                "{}|{}",
                &regions[entry as usize >> 5],
                &isps[entry as usize & 0x1F]
            )));
        }

        // 构建前缀索引表
        let mut pos = index_start;
        for i in 0..PREFIX_COUNT {
            let prefix = (&self.data[pos..pos + 4]).read_u32::<LittleEndian>()?;
            if prefix == i as u32 {
                let bitmap_offset = (&self.data[pos + 4..pos + 8]).read_i32::<LittleEndian>()?;
                let data_offset = (&self.data[pos + 8..pos + 12]).read_i32::<LittleEndian>()?;
                self.index[i] = (bitmap_offset, data_offset);
                pos += 12;
            } else {
                self.index[i] = (0, 0);
            }
        }

        Ok(())
    }

    /// 查询电话号码归属地信息
    pub fn query(&self, phone: &str) -> Option<String> {
        // 解析前缀和后四位
        let prefix = phone[..3].parse::<usize>().ok()?;
        let sub_num = phone[3..7].parse::<usize>().ok()?;

        if prefix >= PREFIX_COUNT {
            return None;
        }

        let (bitmap_offset, data_offset) = self.index[prefix];
        if bitmap_offset == 0 || data_offset == 0 {
            return None;
        }

        let byte_index = sub_num >> 3;
        let bit_index = sub_num & 0b0111;

        let bitmap_offset = bitmap_offset as usize + byte_index;
        if bitmap_offset >= self.data.len() {
            return None;
        }

        let bitmap = self.data[bitmap_offset];
        if (bitmap & (1 << bit_index)) == 0 {
            return None;
        }

        let pop_count_offset = bitmap_offset - byte_index + BITMAP_POP_COUNT_OFFSET + (byte_index << 1);
        let pre_count = (&self.data[pop_count_offset..pop_count_offset + 2])
            .read_u16::<LittleEndian>()
            .ok()?;
        let local_count = (bitmap & ((1 << bit_index) - 1)).count_ones() as u16;

        let data_pos = data_offset as usize + ((pre_count + local_count) << 1) as usize;
        let entry = (&self.data[data_pos..data_pos + 2])
            .read_u16::<LittleEndian>()
            .ok()?;

        if entry < self.region_isps.len() as u16 {
            Some(self.region_isps[entry as usize].to_string())
        } else {
            None
        }
    }
}