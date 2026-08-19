import os
import struct

# qqzeng-phone-4.0.dat 
# python-3.12.2

class PhoneSearch4Binary:
    _instance = None

    def __new__(cls):
        if cls._instance is None:
            cls._instance = super().__new__(cls)
            cls._instance._load_dat()
        return cls._instance

    def _load_dat(self):
        dat_path = os.path.join(os.path.dirname(__file__), 'qqzeng-phone-4.0.dat')

        with open(dat_path, 'rb') as f:
            self.data = f.read()

        pref_size = struct.unpack('<I', self.data[0:4])[0]
        desc_length = struct.unpack('<I', self.data[8:12])[0]
        isp_length = struct.unpack('<I', self.data[12:16])[0]

        head_length = 20
        start_index = head_length + desc_length + isp_length

        desc_string = self.data[head_length:head_length + desc_length].decode('utf-8')
        self.addr_arr = desc_string.split('&')

        isp_string = self.data[head_length + desc_length:head_length + desc_length + isp_length].decode('utf-8')
        self.isp_arr = isp_string.split('&')

        self.pref_dict = {}
        for m in range(pref_size):
            i = m * 5 + start_index
            pref =  self.data[i]
            index = struct.unpack('<I', self.data[i + 1:i + 5])[0]
            self.pref_dict[pref] = index

    def query(self, phone):
        prefix = int(phone[:3])
        suffix = int(phone[3:7])
        addrisp_index = 0

        if prefix in self.pref_dict:
            start = self.pref_dict[prefix]
            p = start + suffix * 2
            addrisp_index = struct.unpack('<H', self.data[p:p + 2])[0]

        if addrisp_index == 0:
            return "|||||"

        return self.addr_arr[addrisp_index >> 5] + "|" + self.isp_arr[addrisp_index & 0x001F]
