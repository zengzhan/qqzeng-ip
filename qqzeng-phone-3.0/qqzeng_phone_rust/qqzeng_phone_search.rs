use std::sync::Mutex;
use lazy_static::lazy_static;



pub struct PhoneSearch {
    phone_2d: Vec<Vec<usize>>,
    addr_arr: Vec<String>,
    isp_arr: Vec<String>,
}

lazy_static! {
    pub static ref PHONE_SEARCH: Mutex<PhoneSearch> = Mutex::new(PhoneSearch::new());
}

impl PhoneSearch {
    pub fn new()-> Self {
        let mut phones = PhoneSearch {
            phone_2d: vec![vec![0; 10000]; 200],
            addr_arr:Vec::new(),
            isp_arr:Vec::new(),
        };
        phones.load_dat();
        phones
    }


    fn load_dat(&mut self) {

        let dat_path = "./qqzeng-phone-3.0.dat";
        let data =  std::fs::read(dat_path).unwrap();
        
        let pref_size = u32::from_le_bytes(data[0..4].try_into().unwrap()) as usize;
        let desc_length = u32::from_le_bytes(data[8..12].try_into().unwrap()) as usize;
        let isp_length = u32::from_le_bytes(data[12..16].try_into().unwrap()) as usize;
      //  let phone_size = u32::from_le_bytes(data[4..8].try_into().unwrap()) as usize;
      //  let ver_num = u32::from_le_bytes(data[16..20].try_into().unwrap())as usize;
        
        let head_length = 20;
        let start_index = (head_length + desc_length + isp_length) as usize;
        
        let mut desc_str = Vec::with_capacity(desc_length);
        desc_str.extend_from_slice(&data[head_length..(head_length + desc_length)]);
        let desc_str = String::from_utf8(desc_str).unwrap();
        self.addr_arr = desc_str.split('&').map(|s| s.to_owned()).collect();
        
        let mut isp_str = Vec::with_capacity(desc_length);
        isp_str.extend_from_slice(&data[head_length + desc_length..(head_length + desc_length + isp_length)]);
        let isp_str = String::from_utf8(isp_str).unwrap();
        self.isp_arr= isp_str.split('&').map(|s| s.to_owned()).collect();
        
     
        for m in 0..pref_size {
            let i = (m * 7 + start_index) as usize;
            let pref = data[i] as usize;
            let index = u32::from_le_bytes(data[(i + 1)..(i + 5)].try_into().unwrap()) as usize;
            let length = u16::from_le_bytes(data[(i + 5)..(i + 7)].try_into().unwrap()) as usize;
            
            for n in 0..length {
                let p = (start_index + (pref_size * 7) as usize + (n + index) * 4) as usize;
                let suff = u16::from_le_bytes(data[p..(p + 2)].try_into().unwrap()) as usize;
                let addrisp_index = u16::from_le_bytes(data[(p + 2)..(p + 4)].try_into().unwrap()) as usize;
                self.phone_2d[pref][suff] = addrisp_index ;
            }
        }
    }


    pub fn query(&self, phone: &str) -> String {
        let prefix = phone[0..3].parse::<usize>().unwrap();
        let suffix = phone[3..7].parse::<usize>().unwrap();
        let addrisp_index = self.phone_2d[prefix][suffix] as  usize;
        
        if addrisp_index == 0 {
            return String::new();
        }
        
        format!("{}|{}", self.addr_arr[addrisp_index / 100], self.isp_arr[addrisp_index % 100])
    }


   
}

