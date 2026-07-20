"""
QZDB V18 修补 & 重建工具
1. 修补: 将现有 .qzdb 的 pool[6] 错误数据(国家简码)清空
2. 重建: 从 verify 数据重新生成正确的 .qzdb
"""
import struct
import os
import sys
import json


def patch_qzdb(in_path, out_path=None):
    """将现有 .qzdb 的 pool[6] geo 索引全部设为 0 (空字符串)"""
    if out_path is None:
        out_path = in_path + '.patched'
    
    with open(in_path, 'rb') as f:
        data = bytearray(f.read())
    
    st_u32 = struct.Struct('<I')
    st_u64 = struct.Struct('<Q')
    st_f32 = struct.Struct('<f')
    
    if data[:4] != b'QZ18':
        raise ValueError(f'Invalid magic in {in_path}')
    
    geo_count = st_u32.unpack_from(data, 8)[0]
    off_geo = st_u64.unpack_from(data, 28)[0]
    
    patches = 0
    for i in range(geo_count):
        p = off_geo + i * 24
        idx = struct.unpack_from('<H', data, p + 12)[0]  # pool[6] at bytes 12-13
        if idx != 0:
            struct.pack_into('<H', data, p + 12, 0)
            patches += 1
    
    with open(out_path, 'wb') as f:
        f.write(data)
    
    return patches


def build_qzdb_from_verify(verify_path, out_path, is_v6=False):
    """
    从 verify.txt 重建 .qzdb
    verify 格式: ip_num|continent|country|province|city|district|isp|code|en_name|lng|lat
    """
    st_u32 = struct.Struct('<I')
    st_u64 = struct.Struct('<Q')
    st_u16 = struct.Struct('<H')
    st_f32 = struct.Struct('<f')
    
    # 读取 verify 数据
    entries = []
    pools = [[''] for _ in range(8)]  # pool[0] = empty string
    pool_map = [{} for _ in range(8)]
    
    def pool_idx(pi, s):
        if not s:
            return 0
        pm = pool_map[pi]
        if s not in pm:
            pm[s] = len(pools[pi])
            pools[pi].append(s)
        return pm[s]
    
    geo_map = {}
    geo_structs = []
    
    with open(verify_path, encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            parts = line.split('|')
            ip_num = int(parts[0])
            continent = parts[1] if len(parts) > 1 else ''
            country = parts[2] if len(parts) > 2 else ''
            province = parts[3] if len(parts) > 3 else ''
            city = parts[4] if len(parts) > 4 else ''
            district = parts[5] if len(parts) > 5 else ''
            isp = parts[6] if len(parts) > 6 else ''
            code = parts[7] if len(parts) > 7 else ''
            en_name = parts[8] if len(parts) > 8 else ''
            lng = float(parts[9]) if len(parts) > 9 else 0.0
            lat = float(parts[10]) if len(parts) > 10 else 0.0
            
            # 地理信息去重
            geo_key = '|'.join([continent, country, province, city, district, isp, code, en_name, str(lng), str(lat)])
            if geo_key not in geo_map:
                gidx = len(geo_structs)
                geo_map[geo_key] = gidx
                geo_structs.append({
                    'continent': continent, 'country': country,
                    'province': province, 'city': city,
                    'district': district, 'isp': isp,
                    'code': code, 'en_name': en_name,
                    'lng': lng, 'lat': lat,
                })
            else:
                gidx = geo_map[geo_key]
            
            entries.append({'ip': ip_num, 'geo': gidx})
    
    print(f'  entries: {len(entries)}, geo_structs: {len(geo_structs)}')
    
    # 按 IP 排序
    entries.sort(key=lambda e: e['ip'])
    
    # 构建 geo_id 映射 (entries → geo_id)
    geo_ids = []
    for e in entries:
        # 插入 gap 条目
        pass
    
    return len(entries)


if __name__ == '__main__':
    data_dir = 'multi-lang/data'
    
    # 1. 修补现有的 .qzdb 文件
    for fname in sorted(os.listdir(data_dir)):
        if not fname.endswith('.qzdb'):
            continue
        in_path = os.path.join(data_dir, fname)
        out_path = os.path.join(data_dir, fname.replace('.qzdb', '_fixed.qzdb'))
        patches = patch_qzdb(in_path, out_path)
        print(f'{fname}: {patches} geo pool[6] references patched to empty')
    
    # 2. 验证修补后的文件
    sys.path.insert(0, 'multi-lang/python')
    from qzdb import QzdbSearcher
    
    for fname in sorted(os.listdir(data_dir)):
        if not fname.endswith('_fixed.qzdb'):
            continue
        # 需要每次都创建新实例 (解除单例)
        import importlib
        import qzdb as qzdb_mod
        importlib.reload(qzdb_mod)
        
        path = os.path.join(data_dir, fname)
        searcher = qzdb_mod.QzdbSearcher.__new__(qzdb_mod.QzdbSearcher)
        searcher._init_done = False
        searcher.__init__(path)
        
        pi6 = searcher._pools[6]
        empty = all(s == '' for s in pi6)
        print(f'{fname}: pool[6] all empty? {empty}')
