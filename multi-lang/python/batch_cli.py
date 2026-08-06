import sys
import os
sys.path.append("/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/python")
import qzdb

if len(sys.argv) < 2:
    sys.exit(1)

searcher = qzdb.QzdbReader(sys.argv[1])
for line in sys.stdin:
    ip = line.strip()
    if not ip:
        continue
    res = searcher.find_str(ip)
    print(res if res is not None else "")
