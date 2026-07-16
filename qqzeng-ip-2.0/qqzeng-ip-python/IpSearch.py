 # -*- coding: utf-8 -*-
 #IP数据库格式详解 qqzeng-ip.dat
 #编码：UTF8  字节序：Little-Endian  
 #返回多个字段信息（如：亚洲|中国|香港|九龙|油尖旺|新世界电讯|810200|Hong Kong|HK|114.17495|22.327115）

import struct,socket,os
try:
    import mmap
except ImportError:
    mmap = None

class IpSearch:   
   
    def __init__(self,file_name):       
        try:
            path = os.path.abspath(file_name)
            self._handle= open(path, "rb")
            if mmap is not None:
                self.data = mmap.mmap(self._handle.fileno(), 0, access=mmap.ACCESS_READ)
            else:
                self.data = self._handle.read()
            
            self.dict={}       
            self.start =self.int_from_4byte(0)
            index_last_offset = self.int_from_4byte(4)
            prefix_start_offset = self.int_from_4byte(8) 
            prefix_end_offset = self.int_from_4byte(12)           
            i=prefix_start_offset
            while i <= prefix_end_offset:                 
                prefix =self.int_from_1byte(i)               
                map_dict={ 'prefix':prefix , 'start_index':self.int_from_4byte(i+1), 'end_index':self.int_from_4byte(i+5) }
                self.dict[prefix]=map_dict
                i+=9
              
        except Exception as ex:
            print "cannot open file %s" % file
            print ex.message
            exit(0)

    def __enter__(self):
        return self

    def __exit__(self):
        self.close()

    def close(self):       
        self._handle.close()
  
    def Find(self,ip): 
        ipdot = ip.split('.')
        prefix=int(ipdot[0])
        if prefix < 0 or prefix > 255 or len(ipdot) != 4:
            return "N/A"       
        intIP = self.ip2long(ip)
        high = 0
        low = 0
        startIp = 0
        endIp = 0
        local_offset = 0
        local_length = 0           
        if self.dict.has_key(prefix):               
            low = self.dict.get(prefix)['start_index']
            high = self.dict.get(prefix)['end_index']       
        else:       
            return "N/A"    
        my_index = self.binarySearch(low, high, intIP)  if low != high else  low
        start_num,end_num,local_offset,local_length=self.getIndex(my_index)
        if  start_num <= intIP  and   end_num >= intIP :       
            return self.getLocal(local_offset, local_length)       
        else:       
            return "N/A" 

    def getIndex(self, left):       
        i = self.start + (left * 12)
        start_num = self.int_from_4byte(i)
        end_num = self.int_from_4byte(4+i)
        local_offset =self.int_from_3byte(8 + i)
        local_length = self.int_from_1byte(11 + i)
        return start_num,end_num,local_offset,local_length

    def getLocal(self, offset, size):      
        return self.data[offset:offset + size].decode('utf-8') 


    def binarySearch(self,low, high, k):
        M=0
        while low<= high:
            mid =(low + high)/2
            end_ip_num = self.get_end_ip_num(mid)
            if end_ip_num >= k:
                M = mid
                if mid ==0:
                   break
                high = mid -1
            else:
                low = mid +1
        return M




    def get_end_ip_num(self,left):
          left_offset = self.start + (left *12)
          return self.int_from_4byte(left_offset+4)
    
    def ip2long(self, ip):
        _ip = socket.inet_aton(ip)
        return struct.unpack("!L", _ip)[0]
  
    def int_from_4byte(self,offset):
        return struct.unpack('<L', self.data[offset:offset + 4])[0]

    def int_from_3byte(self,offset):
        return struct.unpack('<L', self.data[offset:offset + 3]+'\x00' )[0]

    def int_from_1byte(self,offset):
        return struct.unpack('B', self.data[offset:offset + 1])[0]
        
        

