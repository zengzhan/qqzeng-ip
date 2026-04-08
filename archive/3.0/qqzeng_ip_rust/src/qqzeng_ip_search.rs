use std::sync::Mutex;
use lazy_static::lazy_static;




pub struct IPSearch3Span {
    end_arr: Vec<u32>,
    addr_arr: Vec<String>,
    pref_map: [[u32; 2]; 256],
}

lazy_static! {
    pub static ref IP_SEARCH: Mutex<IPSearch3Span> = Mutex::new(IPSearch3Span::new());
}

impl IPSearch3Span {
    pub fn new()-> Self {
        let mut ips = IPSearch3Span {
            pref_map: [[0; 2]; 256],
            end_arr: Vec::new(),
            addr_arr: Vec::new(),
        };
        ips.load_dat();
        ips
    }


    fn load_dat(&mut self) {

        let dat_path = "./qqzeng-ip-3.0-ultimate.dat";
     
        let data = std::fs::read(dat_path).unwrap();
        let span = &data[..];
    

        for k in 0..256 {
            let i = k * 8 + 4;
            let start_index = Self::to_u32(&span[i..(i + 4)]);
            let end_index = Self::to_u32(&span[(i + 4)..(i + 8)]);
            self.pref_map[k][0] = start_index;
            self.pref_map[k][1] = end_index;
        }

        let record_size = Self::to_u32(&span[0..4]) as usize;
        self.end_arr = vec![0; record_size];
        self.addr_arr = vec![String::new(); record_size];
        let mut end_arr_index = 0;
        let mut addr_arr_index = 0;
        let mut p = 2052;
        for _ in 0..record_size {
            let end_ip_num = Self::to_u32(&span[p..(p + 4)]);
            let offset = Self::to_u24(&span[(4 + p)..(7 + p)]) as usize;
            let length = span[7 + p] as usize;
            self.end_arr[end_arr_index] = end_ip_num;
            end_arr_index += 1;
            let mut addr_str = Vec::with_capacity(length);
            addr_str.extend_from_slice(&span[offset..(offset + length)]);
            let addr_str = String::from_utf8(addr_str).unwrap();
            self.addr_arr[addr_arr_index] = addr_str;
            addr_arr_index += 1;
            p += 8;
        }
    }


   
    pub fn query(&self, ip: &str) -> String {
        let (k, prefix) = self.ip_to_int(ip);
        let (low, high) = (self.pref_map[prefix as usize][0], self.pref_map[prefix as usize][1]);
    
        let cur = if low == high {
            low
        } else {
            self.binary_search(low, high, k )
        };
        self.addr_arr[cur as usize].clone()
    }

    fn ip_to_int(&self, ip_str: &str) -> (u32, u32) {
        let parts: Vec<u32> = ip_str
            .split('.')
            .map(|s| s.parse().unwrap_or(0))
            .collect();
        let prefix = parts[0];
        let val =(parts[0] << 24) | (parts[1] << 16) | (parts[2] << 8) | parts[3];
        (val, prefix)
    }

    fn binary_search(&self, low: u32, high: u32, k:u32) -> u32 {
        let mut m = 0;
        let mut l = low;
        let mut h = high;
        while l <= h {
            let mid: u32 = l + ((h - l) >> 1);
            if self.end_arr[mid as usize] >= k {
                m = mid;
                h = mid - 1;
            } else {
                l = mid + 1;
            }
        }
        m
    }

   

    fn to_u32(bytes: &[u8]) -> u32 {
        u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]])
    }
    fn to_u24(bytes: &[u8]) -> u32 {
        u32::from_le_bytes([bytes[0], bytes[1], bytes[2], 0])
    }

   
}

