import os
import sys

# 导入PhoneSearch4Binary类
from phone_search import PhoneSearch4Binary

# 创建PhoneSearch4Binary类的实例
phone_search = PhoneSearch4Binary()

phone_list = ['1522008', '1588760', '1738907']
for index,phone in enumerate(phone_list):
    # 调用query方法进行查询
    result=phone_search.query(phone)
    print(result) 

#广东|深圳|518000|0755|440300|中国移动
#云南|西双版纳|666100|0691|532800|中国移动
#西藏|阿里|859000|0897|542500|中国电信
