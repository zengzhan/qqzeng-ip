

// 手机归属地查询解析 qqzeng-phone-4.0.dat
// rustc 1.76.0   2024-02-27

use std::{
    collections::HashMap,
    fs::File,
    io::{self, Read},
    path::Path,
};


pub struct PhoneSearch4Binary {
    pref_dict: HashMap<String, u32>,
    data: Vec<u8>,
    addr_arr: Vec<String>,
    isp_arr: Vec<String>,
}
impl PhoneSearch4Binary {   
    pub fn new() -> Self {
        PhoneSearch4Binary {
            pref_dict: HashMap::new(),
            data: Vec::new(),
            addr_arr: Vec::new(),
            isp_arr: Vec::new(),
        }
    }

    pub fn load_dat(&mut self) -> io::Result<()> {
        let dat_path = Path::new("qqzeng-phone-4.0.dat");
        let mut file = File::open(dat_path)?;

        file.read_to_end(&mut self.data)?;

        let pref_size = u32::from_le_bytes(self.data[0..4].try_into().unwrap()) as usize;
        let desc_length = u32::from_le_bytes(self.data[8..12].try_into().unwrap()) as usize;
        let isp_length = u32::from_le_bytes(self.data[12..16].try_into().unwrap()) as usize;
        let _ver_num = u32::from_le_bytes(self.data[16..20].try_into().unwrap()) as usize;

        let head_length = 20;
        let start_index = head_length + desc_length + isp_length;

        let desc_string = String::from_utf8_lossy(&self.data[head_length..(head_length + desc_length)]);
        self.addr_arr = desc_string.split('&').map(String::from).collect();

        let isp_string =
            String::from_utf8_lossy(&self.data[(head_length + desc_length)..(head_length + desc_length + isp_length)]);
        self.isp_arr = isp_string.split('&').map(String::from).collect();

        for m in 0..pref_size {
            let i = m * 5 + start_index;
            let pref = self.data[i] as u32;
            let index = u32::from_le_bytes(self.data[(i + 1)..(i + 5)].try_into().unwrap()) as u32;
            self.pref_dict.insert(pref.to_string(), index);
        }

        Ok(())
    }

    pub fn query(&self, phone: &str) -> String {
        let prefix = &phone[0..3];
        let suffix = phone[3..7].parse::<u32>().unwrap();
        let mut addrisp_index = 0;

        match self.pref_dict.get(prefix) {
            Some(&start) => {
                let p = (start + suffix * 2) as usize;
                addrisp_index = u16::from_le_bytes(self.data[p..(p + 2)].try_into().unwrap());
            }
            None => {}
        }

        if addrisp_index == 0 {
            return "不存在".to_string();
        }

        format!(
            "{}|{}",
            self.addr_arr[(addrisp_index >> 5) as usize],
            self.isp_arr[(addrisp_index & 0x001F) as usize]
        )
    }

}



   


