import csv, random, ipaddress, os, sys

sys.path.insert(0, os.path.dirname(__file__))
from qzdb import QzdbSearcher

BASE_DIR = os.path.join(os.path.dirname(__file__), '..', '..')
DATA_DIR = os.path.join(os.path.dirname(__file__), '..', 'data')

def geo_to_str(g):
    if g is None:
        return ''
    return '|'.join([g.continent, g.country, g.province, g.city,
                     g.district, g.isp, g.code, g.en_name,
                     f'{g.lng:.6f}', f'{g.lat:.6f}'])

def generate(csv_rel, db_name, sample_v4, sample_v6, v4_out, v6_out):
    csv_path = os.path.join(BASE_DIR, csv_rel)
    qzdb_path = os.path.join(BASE_DIR, f'qqzeng_ip_{db_name}.qzdb')

    if not os.path.exists(csv_path):
        print(f'  SKIP: {csv_path} not found')
        return
    if not os.path.exists(qzdb_path):
        print(f'  SKIP: {qzdb_path} not found')
        return

    s = QzdbSearcher()
    s.load(qzdb_path)
    rng = random.Random(42)

    v4_cases = set()
    v6_cases = set()

    v4_idx = 0
    v6_idx = 0
    v4_idx = 0
    v6_idx = 0
    with open(csv_path, 'r', encoding='utf-8') as f:
        reader = csv.reader(f)
        header = next(reader)

        for i, row in enumerate(reader):
            cidr = row[0]
            try:
                if ':' in cidr:
                    if v6_idx % sample_v6 != 0:
                        v6_idx += 1
                        continue
                    v6_idx += 1
                    net = ipaddress.IPv6Network(cidr, strict=False)
                    if net.num_addresses == 0:
                        continue
                    offset = rng.randint(0, min(net.num_addresses - 1, 0xFFFFFFFF))
                    ip_int = int(net[offset])
                    high = (ip_int >> 64) & 0xFFFFFFFFFFFFFFFF
                    low = ip_int & 0xFFFFFFFFFFFFFFFF
                    g = s._find_v6(high, low)
                    v6_cases.add((high, low, g))
                else:
                    if v4_idx % sample_v4 != 0:
                        v4_idx += 1
                        continue
                    v4_idx += 1
                    net = ipaddress.IPv4Network(cidr, strict=False)
                    if net.num_addresses == 0:
                        continue
                    ip_test = int(net[rng.randint(0, net.num_addresses - 1)])
                    if ip_test >= 2**32:
                        continue
                    g = s.find_uint(ip_test)
                    v4_cases.add((ip_test, g))
            except Exception:
                continue

    v4_path = os.path.join(DATA_DIR, v4_out)
    with open(v4_path, 'w') as f:
        for ip, g in sorted(v4_cases):
            f.write(f'{ip}|{geo_to_str(g)}\n')
    print(f'  V4: {len(v4_cases)} cases -> {v4_out}')

    v6_path = os.path.join(DATA_DIR, v6_out)
    with open(v6_path, 'w') as f:
        for high, low, g in v6_cases:
            f.write(f'{high}:{low}|{geo_to_str(g)}\n')
    print(f'  V6: {len(v6_cases)} cases -> {v6_out}')


def main():
    print('Generating max_china verification files...')
    generate('qqzeng_ip_max_china.csv', 'max_china', 50, 100,
             'verify_max_china_v4.txt', 'verify_max_china_v6.txt')

    print('Generating max_global verification files...')
    generate('qqzeng_ip_max_global.csv', 'max_global', 200, 200,
             'verify_max_global_v4.txt', 'verify_max_global_v6.txt')


if __name__ == '__main__':
    main()
