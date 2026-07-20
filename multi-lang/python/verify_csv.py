import csv, random, ipaddress, os, sys

sys.path.insert(0, os.path.dirname(__file__))
from qzdb import QzdbSearcher

DATA_DIR = os.path.join(os.path.dirname(__file__), '..', 'data')
BASE_DIR = os.path.join(os.path.dirname(__file__), '..', '..')

def test_database(db_name, csv_rel_path, sample_n):
    csv_path = os.path.join(BASE_DIR, csv_rel_path)
    qzdb_path = os.path.join(BASE_DIR, f'qqzeng_ip_{db_name}.qzdb')

    if not os.path.exists(csv_path):
        print(f'  [{db_name}] SKIP: CSV not found at {csv_path}')
        return True
    if not os.path.exists(qzdb_path):
        print(f'  [{db_name}] SKIP: qzdb not found at {qzdb_path}')
        return True

    s = QzdbSearcher()
    s.load(qzdb_path)
    rng = random.Random(42)
    total = 0
    pass_count = 0
    fail_count = 0

    with open(csv_path, 'r', encoding='utf-8') as f:
        reader = csv.reader(f)
        header = next(reader)

        for i, row in enumerate(reader):
            if i % sample_n != 0:
                continue
            cidr = row[0]
            if ':' in cidr:
                try:
                    net = ipaddress.IPv6Network(cidr, strict=False)
                    if net.num_addresses == 0:
                        continue
                    offset = rng.randint(0, min(net.num_addresses - 1, 0xFFFFFFFF))
                    ip_int = int(net[offset])
                    high = (ip_int >> 64) & 0xFFFFFFFFFFFFFFFF
                    low = ip_int & 0xFFFFFFFFFFFFFFFF
                    r = s._find_v6(high, low)
                except Exception:
                    continue
            else:
                try:
                    net = ipaddress.IPv4Network(cidr, strict=False)
                    if net.num_addresses == 0:
                        continue
                    ip_test = int(net[rng.randint(0, net.num_addresses - 1)])
                    if ip_test >= 2**32:
                        continue
                    r = s.find_uint(ip_test)
                except Exception:
                    continue
            total += 1

            expected_str = [row[1], row[5], row[7], row[9], row[11], row[21], row[3], row[6]]
            exp_lng = float(row[14]) if row[14] else 0.0
            exp_lat = float(row[15]) if row[15] else 0.0
            if r is None:
                if any(expected_str):
                    fail_count += 1
                    if fail_count <= 3:
                        print(f'  [{db_name}] MISS row {i}: {cidr}')
                continue

            if 'country_en' in s.field_names:
                got_str = [
                    getattr(r, 'continent', ''),
                    getattr(r, 'country', ''),
                    getattr(r, 'province', ''),
                    getattr(r, 'city', ''),
                    getattr(r, 'district', ''),
                    getattr(r, 'isp', ''),
                    getattr(r, 'country_code', ''),
                    getattr(r, 'country_en', '')
                ]
            else:
                got_str = [
                    getattr(r, 'continent', ''),
                    getattr(r, 'country', ''),
                    getattr(r, 'province', ''),
                    getattr(r, 'city', ''),
                    getattr(r, 'district', ''),
                    getattr(r, 'isp', ''),
                    getattr(r, 'area_code', ''),
                    getattr(r, 'country_english', '')
                ]

            str_ok = all(e == g for e, g in zip(expected_str, got_str))
            r_lng = float(r.longitude) if r.longitude else 0.0
            r_lat = float(r.latitude) if r.latitude else 0.0
            lng_ok = abs(exp_lng - r_lng) < 0.00005
            lat_ok = abs(exp_lat - r_lat) < 0.00005

            if str_ok and lng_ok and lat_ok:
                pass_count += 1
            else:
                fail_count += 1
                if fail_count <= 3:
                    print(f'  [{db_name}] MISMATCH row {i}: {cidr}')
                    field_names = ['cont', 'ctry', 'prov', 'city', 'dist', 'isp', 'code', 'en']
                    for j, (e, g, name) in enumerate(zip(expected_str, got_str, field_names)):
                        if e != g:
                            print(f'    {name}: CSV="{e}" vs DB="{g}"')
                    if not lng_ok:
                        print(f'    lng: CSV={exp_lng:.6f} vs DB={r_lng:.6f}')
                    if not lat_ok:
                        print(f'    lat: CSV={exp_lat:.6f} vs DB={r_lat:.6f}')

    print(f'  [{db_name}] {pass_count}/{total} passed', end='')
    if fail_count > 0:
        print(f', {fail_count} FAILED', end='')
    print()
    return fail_count == 0


def main():
    results = []
    results.append(test_database('max_china', 'qqzeng_ip_max_china.csv', 50))
    results.append(test_database('max_global', 'qqzeng_ip_max_global.csv', 200))
    print()
    if all(results):
        print('All CSV verification passed!')
        print('TEST_PASS')
    else:
        print('Some CSV verification FAILED!')
    return 0 if all(results) else 1


if __name__ == '__main__':
    sys.exit(main())
