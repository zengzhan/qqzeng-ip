import os
import sys

from PhoneSearch import PhoneSearch

finder = PhoneSearch('qqzeng-phone-3.0.dat')

phone_list = ['1522008', '1588760', '1738907']
for index,phone in enumerate(phone_list):
    result=finder.search(phone)
    print(result) 

#广东|深圳|518000|0755|440300|移动
#云南|西双版纳|666100|0691|532800|移动
#西藏|阿里|859000|0897|542500|电信
