from qqzeng_ip.ipdb import IpDbSearch
import time
import os

def main():
    print("正在初始化 qqzeng-ip 数据库...")
    start = time.time()
    searcher = IpDbSearch()
    elapsed = (time.time() - start) * 1000
    print(f"数据库加载完成，耗时: {elapsed:.2f} ms")

    test_file = find_test_file()
    if not test_file:
        print("无法找到测试文件")
        return

    print(f"正在读取测试文件: {test_file}")
    with open(test_file, 'r', encoding='utf-8') as f:
        lines = [line.strip() for line in f if line.strip()]
    
    print(f"共有 {len(lines)} 条测试记录")

    passed = 0
    failed = 0

    # 预热
    searcher.find("8.8.8.8")

    test_start = time.time()
    for line in lines:
        parts = line.split('\t')
        if len(parts) < 3: continue

        start_ip = parts[0]
        end_ip = parts[1]
        expected = parts[2]

        if not verify(searcher, start_ip, expected):
            failed += 1
            continue
        if not verify(searcher, end_ip, expected):
            failed += 1
            continue
            
        mid_ip = get_mid_ip(start_ip, end_ip)
        if not verify(searcher, mid_ip, expected):
            failed += 1
            continue

        passed += 1

    test_elapsed = (time.time() - test_start) * 1000
    
    print("\n-------------------------------------------")
    print("测试完成!")
    print(f"总记录数: {len(lines)}")
    print(f"通过: {passed}")
    print(f"失败: {failed}")
    print(f"总耗时: {test_elapsed:.2f} ms")
    if len(lines) > 0:
        print(f"平均耗时: {test_elapsed / (len(lines)*3):.4f} ms/query")
    print("-------------------------------------------")

    # 压测
    print("\n开始性能压测 (1,000,000 次查询)...")
    bench_start = time.time()
    for _ in range(1000000):
        searcher.find("1.0.0.1")
        searcher.find("255.255.255.255")
        searcher.find("114.114.114.114")
        searcher.find("8.8.8.8")
    
    bench_elapsed = time.time() - bench_start
    print(f"4,000,000 次查询耗时: {bench_elapsed*1000:.2f} ms")
    print(f"QPS: {4000000 / bench_elapsed:.2f}")

def verify(searcher, ip, expected):
    result = searcher.find(ip)
    if result != expected:
        print(f"[Fail] IP: {ip}")
        print(f"  期望: {expected}")
        print(f"  实际: {result}")
        return False
    return True

def find_test_file():
    base_dir = os.path.dirname(os.path.abspath(__file__))
    attempts = [
        os.path.join(base_dir, '../data/test.txt'),
        os.path.join(base_dir, '../../data/test.txt'),
        os.path.join(base_dir, '../../../data/test.txt'),
        '../data/test.txt'
    ]
    for p in attempts:
        if os.path.exists(p): return p
    return None

import socket
import struct

def get_mid_ip(start_ip, end_ip):
    s = struct.unpack('>I', socket.inet_aton(start_ip))[0]
    e = struct.unpack('>I', socket.inet_aton(end_ip))[0]
    if s == e: return start_ip
    mid = s + (e - s) // 2
    return socket.inet_ntoa(struct.pack('>I', mid))

if __name__ == '__main__':
    main()
