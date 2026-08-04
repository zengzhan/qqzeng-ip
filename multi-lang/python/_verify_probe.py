import sys, time
sys.path.insert(0, "/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/python")
import qzdb

DB = "/Users/zengxiangzhan/ZengData/qqzeng-data/temp_work/qqzeng_ip_asn/qqzeng_ip_asn_china.qzdb"
TEMP = "/Users/zengxiangzhan/ZengData/qqzeng-data/temp_work/qqzeng_ip_asn/temp_china_v4.txt"

db = qzdb.QzdbSearcher(db_path=DB)

# 1) Schema state after fix
print("== schema state ==")
print("off_row_schema   =", db._off_row_schema)
print("ip_row_size      =", db._ip_row_size)
print("row_geo_width    =", db._row_geo_width)
print("row_asn_width    =", db._row_asn_width)
print("row_usage_width  =", db._row_usage_width)
print("group_dim_masks  =", db._group_dim_masks)
print("field_names      =", db._field_names)
print("row_count        =", db._row_count)

# 2) Probe sample IPs
samples = ["1.1.8.0", "1.2.4.8", "1.8.1.0", "114.114.114.114", "223.5.5.5", "1.0.1.0", "8.8.8.8"]
print("\n== sample probes (find) ==")
for ip in samples:
    info = db.find(ip)
    print(f"{ip:18} -> {info}")

# 3) Probe raw row ids + ids for the same samples
print("\n== sample probes (row/ids) ==")
for ip in samples:
    rid = db.lookup_row_id(ip)
    ids = db.lookup_ids(rid) if rid else None
    print(f"{ip:18} -> row_id={rid} ids={ids}")
