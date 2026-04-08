import mmap
import struct
import socket
import os

#python 3.10
class PhoneSearch:
    def __init__(self, file_name):
        self._handle = open(file_name, "rb")
        self.data = mmap.mmap(self._handle.fileno(), 0,access=mmap.ACCESS_READ)
       
      
        pref_size = self.int_from_4byte(0)
        phone_size = self.int_from_4byte(4)
        addr_len = self.int_from_4byte(8)
        isp_len = self.int_from_4byte(12)
        ver_num = self.int_from_4byte(16)
        head_len = 20
        start_index = head_len + addr_len + isp_len

        self.addrArr =self.data[head_len:head_len + addr_len].decode('utf-8').split('&')
        self.ispArr =self.data[head_len + addr_len:head_len + addr_len+isp_len].decode('utf-8').split('&')

        self.phone2D =[[ 0 for col in range(10000) ] for row in range(200) ]
        for m in range(0,pref_size):
            i = m * 7 + start_index
            pref = self.int_from_1byte(i)
            index = self.int_from_4byte(i + 1)
            length =self.int_from_2byte(i + 5)
          
            for n in range(0,length):
               p = start_index + pref_size * 7 + (n + index) * 4
               suff = self.int_from_2byte( p)
               addrispIndex=self.int_from_2byte(p + 2)              
               self.phone2D[pref][suff] =addrispIndex
          
 

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_value, exc_tb):
        self.close()

    def close(self):
        self._handle.close()

    def search(self, phone):
       prefix =int(phone[0:3])
       suffix =int(phone[3:7])
       addrispIndex = self.phone2D[prefix][suffix]
       if addrispIndex == 0:
            return ''
       else:
          return self.addrArr[int(addrispIndex / 100)] + "|" +  self.ispArr[int(addrispIndex % 100)]

    
 


    def int_from_4byte(self, offset):
        return  int.from_bytes(self.data[offset:offset + 4], "little") 

    def int_from_3byte(self, offset):
        return  int.from_bytes(self.data[offset:offset + 3], "little") 

    def int_from_2byte(self, offset):
       return  int.from_bytes(self.data[offset:offset + 2], "little") 

    def int_from_1byte(self, offset):
        return  int.from_bytes(self.data[offset:offset + 1], "little") 
