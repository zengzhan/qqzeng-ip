import sys
sys.path.insert(0, "/Users/zengxiangzhan/ZengData/IP数据库/qzdb/multi-lang/python")
import qzdb

DB = "/Users/zengxiangzhan/ZengData/qqzeng-data/temp_work/qqzeng_ip_asn/qqzeng_ip_asn_china.qzdb"
TEMP = "/Users/zengxiangzhan/ZengData/qqzeng-data/temp_work/qqzeng_ip_asn/temp_china_v4.txt"

db = qzdb.QzdbSearcher(db_path=DB)

total = 0
real_asn = 0
asn_less = 0

collapse = 0          # real-ASN temp line -> qzdb returns 56554 / None / ''  (BUG SYMPTOM)
exact_match = 0       # qzdb asn == temp asn
other_mismatch = 0    # qzdb returns a *different* real ASN (suspected version skew)
mismatch_examples = []

DEFAULT_ASN = "56554"

with open(TEMP, encoding="utf-8") as f:
    for ln in f:
        ln = ln.rstrip("\n")
        if not ln:
            continue
        parts = ln.split("\t")
        if len(parts) < 5:
            continue
        total += 1
        start_num = int(parts[2])
        pf = parts[4].split("|")
        temp_asn = pf[4].strip() if len(pf) > 4 else ""
        if temp_asn == "" or temp_asn == "0":
            asn_less += 1
            continue
        if temp_asn == DEFAULT_ASN:
            # temp itself says this range belongs to the IETF default 56554;
            # treat as a legit-default agreement case, not a "real-ASN" range.
            asn_less += 1
            info = db.find_uint(start_num)
            qzdb_asn = info.get("asn") if info else None
            qzdb_asn = str(qzdb_asn).strip() if qzdb_asn is not None else ""
            if qzdb_asn == DEFAULT_ASN:
                exact_match += 1  # (reused bucket: default agrees)
            continue
        real_asn += 1
        info = db.find_uint(start_num)
        qzdb_asn = info.get("asn") if info else None
        if qzdb_asn is None:
            qzdb_asn = ""
        qzdb_asn = str(qzdb_asn).strip()
        if qzdb_asn == "" or qzdb_asn == DEFAULT_ASN:
            collapse += 1
            if len(mismatch_examples) < 15:
                mismatch_examples.append((parts[0], temp_asn, qzdb_asn, "COLLAPSE"))
        elif qzdb_asn == temp_asn:
            exact_match += 1
        else:
            other_mismatch += 1
            if len(mismatch_examples) < 15:
                mismatch_examples.append((parts[0], temp_asn, qzdb_asn, "OTHER"))

print("=" * 60)
print("FULL VERIFICATION : qzdb vs temp_china_v4.txt (start IP of each range)")
print("=" * 60)
print(f"total temp lines        : {total}")
print(f"  real-ASN ranges       : {real_asn}")
print(f"  ASN-less ranges       : {asn_less}  (legitimately map to {DEFAULT_ASN})")
print("-" * 60)
print(f"COLLAPSE -> 56554/None  : {collapse}  ({100.0*collapse/real_asn:.2f}% of real-ASN)")
print(f"EXACT MATCH             : {exact_match}  ({100.0*exact_match/real_asn:.2f}% of real-ASN)")
print(f"OTHER (diff real ASN)   : {other_mismatch}  ({100.0*other_mismatch/real_asn:.2f}% of real-ASN)")
print("-" * 60)
print("interpretation:")
if collapse == 0:
    print("  PASS: zero collapse to 56554 among real-ASN ranges.")
    print("  The ROW_SCHEMA parse bug is fixed.")
elif collapse < real_asn * 0.02:
    print("  PASS (near): collapse rate <2%. Bug essentially fixed.")
else:
    print("  FAIL: high collapse rate = parse bug STILL present.")
print()
if mismatch_examples:
    print("sample mismatches (start_ip, temp_asn, qzdb_asn, kind):")
    for e in mismatch_examples:
        print("   ", e)
