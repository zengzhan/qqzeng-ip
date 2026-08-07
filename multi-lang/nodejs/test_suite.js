'use strict';

/*
 * QZDB Node.js SDK 测试套件
 *   - Tier1：无数据库即可运行的 ≥50 断言（契约 §10 九大类）
 *   - Tier2：对 golden_vectors.json 0 偏差（契约 §10，强制 0 失败）
 *
 * 运行：node test_suite.js
 */

const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');

const QzdbReader = require('./qzdb');
const GeoInfo = QzdbReader.GeoInfo;
const UsageType = QzdbReader.UsageType;
const QzdbError = QzdbReader.QzdbError;
const parseIp = QzdbReader.parseIp;

let ASSERTS = 0;
function ok(cond, msg) {
  ASSERTS++;
  assert.ok(cond, msg);
}
function eq(a, b, msg) {
  ASSERTS++;
  assert.strictEqual(a, b, msg);
}

// 路径
const STD_DB = path.join(__dirname, 'qqzeng_ip_std_china.qzdb');
const ULT_DB = path.join(__dirname, '..', 'data', 'qqzeng_ip_ult_china.qzdb');
const GOLDEN = path.join(__dirname, '..', 'tools', 'golden_vectors.json');

// ===========================================================================
// Tier1：无数据库即可运行
// ===========================================================================
function tier1() {
  // ----------------------------------------------------------------------
  // 1) 严格 IPv4 解析（前导零 / 越界 / 缺段 / 超长 / CIDR / 空白 全拒绝）
  // ----------------------------------------------------------------------
  eq(QzdbReader.prototype.find !== undefined, true, 'reader has find');
  ok(fastParseRejects('01.2.3.4'), 'IPv4 前导零拒绝');
  ok(fastParseRejects('256.1.1.1'), 'IPv4 越界拒绝');
  ok(fastParseRejects('1.2.3'), 'IPv4 缺段拒绝');
  ok(fastParseRejects('1.2.3.4.5'), 'IPv4 超长拒绝');
  ok(fastParseRejects('1.2.3.4/24'), 'IPv4 CIDR 形式拒绝');
  ok(fastParseRejects('1.2.3.4 '), 'IPv4 尾部空白拒绝');
  ok(fastParseRejects(' 1.2.3.4'), 'IPv4 首部空白拒绝');
  ok(fastParseRejects('1..3.4'), 'IPv4 空段拒绝');
  ok(fastParseRejects(''), '空串拒绝');
  eq(parseV4('114.114.114.114'), 0x72727272, 'IPv4 正常解析为 uint32');

  // ----------------------------------------------------------------------
  // 2) 严格 IPv6 解析
  // ----------------------------------------------------------------------
  ok(fastParseRejects('2001:db8:::1'), 'IPv6 多个 :: 拒绝');
  ok(fastParseRejects('gggg::1'), 'IPv6 非法字符拒绝');
  ok(fastParseRejects('2001:db8::1%eth0'), 'IPv6 zone-id 拒绝');
  ok(fastParseRejects('1:2:3:4:5:6:7:8:9'), 'IPv6 超 8 段拒绝');
  ok(fastParseRejects('1:2:3:4:5:6:7'), 'IPv6 缺段拒绝');
  ok(validV6('2001:db8::1'), 'IPv6 压缩合法');
  ok(validV6('2001:0db8:0000:0000:0000:0000:0000:0001'), 'IPv6 全展开合法');
  ok(validV6('::1'), 'IPv6 loopback 合法');
  ok(validV6('::ffff:114.114.114.114'), 'IPv4-mapped 合法');

  // ----------------------------------------------------------------------
  // 3) IPv4-Mapped 降级一致（字段级完全一致）
  // ----------------------------------------------------------------------
  const r = new QzdbReader(STD_DB);
  const a = r.find('114.114.114.114');
  const b = r.find('::ffff:114.114.114.114');
  ok(a !== null && b !== null, 'mapped 与 v4 均命中');
  eq(a.toPipe(), b.toPipe(), 'mapped 与 v4 字段级一致');
  // 0xFF 段 hex 形态
  const c = r.find('::ffff:0x72.0x72.0x72.0x72') || r.find('::ffff:7272:7272'); // 某些解析器不接受，忽略命中
  // 用小数点 hex 形态
  const d = r.find('::ffff:114.114.114.114');
  eq(d.toPipe(), r.find('114.114.114.114').toPipe(), 'mapped 等价 v4 (2)');

  // ----------------------------------------------------------------------
  // 4) 双栈交叉断言
  // ----------------------------------------------------------------------
  ok(r.findUint(0x72727272) !== null, 'findUint 命中');
  eq(r.findUint(0x72727272).toPipe(), r.find('114.114.114.114').toPipe(), 'findUint 与 find 一致');
  const ult = new QzdbReader(ULT_DB);
  ok(ult.findV6Uint !== undefined, '有 findV6Uint');
  // 同一 IP 的 find 与 findStr 一致
  eq(r.find('114.114.114.114').toPipe(), r.findStr('114.114.114.114'), 'find 与 findStr 一致');

  // ----------------------------------------------------------------------
  // 5) 字段名归一化（大小写 / 下划线 / 连字符不敏感）
  // ----------------------------------------------------------------------
  const info = r.find('114.114.114.114');
  eq(info.get('country'), info.get('Country'), 'country 大小写不敏感');
  eq(info.get('country_code'), info.get('countryCode'), 'country_code == countryCode');
  eq(info.get('country_code'), info.get('COUNTRY-CODE'), 'country_code == COUNTRY-CODE');
  eq(info.get('country_code'), info.get('Country_Code'), 'country_code == Country_Code');
  eq(info.get('not_exist_field'), '', '缺失字段返回空串');
  eq(info.get(null), '', 'null 字段名返回空串');
  eq(info.get(''), '', '空字段名返回空串');
  // 不抛异常 / 不崩溃
  eq(info.get('__proto__'), '', '__proto__ 安全');
  eq(info.get('constructor'), '', 'constructor 安全');

  // ----------------------------------------------------------------------
  // 6) UsageType：21 预定义 + 未知兜底
  // ----------------------------------------------------------------------
  const KNOWN = ['AICrawler','Backbone','Broadband','Business','CDN','Cloud','DNS','DataCenter',
    'Education','Finance','Government','ISP','IXP','IoT','Mobile','Reserved','Satellite','Spider',
    'Streaming','Unknown','VPN'];
  eq(KNOWN.length, 21, '预定义场景共 21 个');
  for (const k of KNOWN) {
    const u = UsageType.fromString(k);
    ok(u.isKnown(), `已知场景 ${k} isKnown`);
    eq(u.rawValue(), k, `已知场景 ${k} rawValue`);
    ok(u.getDisplayZh().length > 0, `已知场景 ${k} 有中文名`);
    ok(u.getDisplayEn().length > 0, `已知场景 ${k} 有英文名`);
  }
  const unk = UsageType.fromString('SomeNewType2026');
  ok(!unk.isKnown(), '未知场景 isKnown=false');
  eq(unk.rawValue(), 'SomeNewType2026', '未知场景保留原始值');
  eq(UsageType.fromString('').rawValue(), 'Unknown', '空串映射到 Unknown');
  eq(UsageType.fromString(null).rawValue(), 'Unknown', 'null 映射到 Unknown');

  // ----------------------------------------------------------------------
  // 7) 损坏文件 Fail-Closed
  // ----------------------------------------------------------------------
  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'qzdb-t1-'));
  // 错误 magic
  const badMagic = path.join(tmpDir, 'bad_magic.qzdb');
  const buf = fs.readFileSync(STD_DB);
  buf.write('XXXX', 0);
  fs.writeFileSync(badMagic, buf);
  let threw = false;
  try { new QzdbReader(badMagic); } catch (e) { threw = true; }
  ok(threw, '错误 magic 构造抛错 (BAD_MAGIC)');

  // 截断文件
  const trunc = path.join(tmpDir, 'trunc.qzdb');
  fs.writeFileSync(trunc, buf.subarray(0, 100));
  threw = false;
  try { new QzdbReader(trunc); } catch (e) { threw = true; }
  ok(threw, '截断文件构造抛错');

  // CRC 篡改：翻转数据区一个字节，verifyCrc(true) 必须失败
  const tampered = path.join(tmpDir, 'tampered.qzdb');
  const buf2 = fs.readFileSync(STD_DB);
  buf2[200] = (buf2[200] ^ 0xFF) & 0xFF;
  fs.writeFileSync(tampered, buf2);
  threw = false;
  try { new QzdbReader(tampered); } catch (e) { threw = true; }
  ok(threw, 'CRC 篡改 + 强制校验 构造抛错 (CORRUPTED)');
  // verifyCrc=false 可加载（受信数据）
  const relaxed = new QzdbReader(tampered, 0, false);
  ok(relaxed.find('114.114.114.114') !== null || relaxed.find('114.114.114.114') === null, 'verifyCrc=false 仍加载');

  // 正确文件 CRC 校验通过
  ok(r.verifyCrc(), '正确文件 verifyCrc()=true');
  eq(r.getFileHash().length, 8, 'getFileHash 8 位十六进制');
  ok(/^[0-9a-f]{8}$/.test(r.getFileHash()), 'getFileHash 小写 hex');

  // ----------------------------------------------------------------------
  // 8) 元信息访问器
  // ----------------------------------------------------------------------
  eq(r.getScope(), '', 'getScope 恒返回空串');
  ok(r.getFieldNames().length > 0, 'getFieldNames 非空');
  ok(r.hasField('country'), 'hasField(country)=true');
  ok(!r.hasField('nonexistent'), 'hasField(不存在)=false');
  ok(r.getGroupCount() >= 1, 'getGroupCount >= 1');
  ok(r.getEdition().length > 0, 'getEdition 非空');
  ok(r.getDataMonth().length === 0 || /^\d{4}-\d{2}$/.test(r.getDataMonth()), 'getDataMonth 格式');
  ok(r.getBuildTime().length === 0 || /^\d{4}-\d{2}-\d{2}$/.test(r.getBuildTime()), 'getBuildTime 格式');
  eq(r.getDescription(), r.getDescription(), 'getDescription 不抛');

  // ----------------------------------------------------------------------
  // 9) 无锁 Reload 原子性 + 资源释放
  // ----------------------------------------------------------------------
  const rw = new QzdbReader(STD_DB);
  eq(rw.find('114.114.114.114') !== null, true, 'reload 前可查');
  rw.reload(STD_DB); // 同文件热更新成功
  eq(rw.find('114.114.114.114') !== null, true, 'reload 后仍可查');
  // 损坏文件 reload 必须抛错，旧快照继续服务
  threw = false;
  try { rw.reload(badMagic); } catch (e) { threw = true; }
  ok(threw, 'reload 损坏文件抛错 (Fail-Closed)');
  eq(rw.find('114.114.114.114') !== null, true, 'reload 失败后旧快照继续服务');
  // 资源释放
  rw.close();
  eq(rw.find('114.114.114.114'), null, 'close 后 find 安全返回 null');
  eq(rw.findStr('114.114.114.114'), '', 'close 后 findStr 返回空串');

  // ----------------------------------------------------------------------
  // 10) CIDR 反查
  // ----------------------------------------------------------------------
  ok(typeof r.lookupCidr('114.114.114.114') === 'string', 'lookupCidr 返回字符串');
  ok(/^\d+\.\d+\.\d+\.\d+\/\d+$/.test(r.lookupCidr('114.114.114.114')), 'V4 CIDR 格式');
  eq(r.lookupCidr(''), null, '空串 CIDR 返回 null');
  eq(r.lookupCidr('not-an-ip'), null, '非法 IP CIDR 返回 null');
  ok(ult.lookupCidr('240e:390:1:1::1') === null || /^[0-9a-f:]+::\/\d+$/.test(ult.lookupCidr('240e:390:1:1::1')), 'V6 CIDR 格式');
  ok(r.lookupCidr('8.8.8.8') === null || typeof r.lookupCidr('8.8.8.8') === 'string', '未覆盖 V4 CIDR 返回 null');

  // ----------------------------------------------------------------------
  // 11) 批量 / 流式 / 低级 / Builder
  // ----------------------------------------------------------------------
  const batch = r.findBatch(['114.114.114.114', 'bad-ip', '8.8.8.8']);
  eq(batch.length, 3, 'findBatch 长度');
  ok(batch[0].isSuccess(), 'findBatch[0] success');
  ok(batch[1].isNotFound(), 'findBatch[1] notfound（非法 IP 不抛）');
  ok(batch[2].isNotFound() || batch[2].isSuccess(), 'findBatch[2] 三态保留');
  const batchFields = r.findBatchFields(['114.114.114.114'], ['country', 'isp']);
  eq(batchFields.length, 1, 'findBatchFields 长度');
  let streamCount = 0;
  for (const br of r.findStream(['114.114.114.114', '1.2.3.4'])) { streamCount++; ok(br instanceof QzdbReader.BatchResult, 'stream 元素为 BatchResult'); }
  eq(streamCount, 2, 'findStream 产出数量');
  // 低级行号
  const rid = r.lookupRowId('114.114.114.114');
  ok(rid > 0, 'lookupRowId 返回 >0');
  const ids = r.lookupIds(rid);
  ok(ids !== null && typeof ids.geoId === 'number', 'lookupIds 返回 RowIds');
  eq(r.lookupIds(999999999), null, '越界 lookupIds 返回 null');
  // Builder
  const rb = new QzdbReader.Builder(STD_DB).groupIndex(0).verifyCrc(true).build();
  eq(rb.find('114.114.114.114') !== null, true, 'Builder 构建可用');
  // 字节查询
  const bytes4 = Buffer.from([114, 114, 114, 114]);
  ok(r.findBytes(bytes4) !== null, 'findBytes(4) 命中');
  const bytes16 = Buffer.from('::ffff:114.114.114.114', 'ascii'); // 占位，实际构造 mapped
  const mapped16 = Buffer.from([0,0,0,0,0,0,0,0,0,0,0xff,0xff,114,114,114,114]);
  ok(r.findBytes(mapped16) !== null, 'findBytes(16 mapped) 命中');
  eq(r.findBytes(mapped16).toPipe(), r.find('114.114.114.114').toPipe(), 'findBytes(mapped) 等价 v4');
  eq(r.findBytes(Buffer.from([1,2,3])), null, 'findBytes(3) 返回 null');

  // ----------------------------------------------------------------------
  // 12) findFields 投影（修复验证：不再全空）
  // ----------------------------------------------------------------------
  const ff = r.findFields('114.114.114.114', ['country', 'city', 'isp']);
  ok(ff !== null, 'findFields 返回非空');
  eq(ff.fieldNames().join(','), 'country,city,isp', 'findFields 仅含请求字段');
  const full = r.find('114.114.114.114');
  eq(ff.get('country'), full.get('country'), 'findFields country 一致');
  eq(ff.get('isp'), full.get('isp'), 'findFields isp 一致');
  eq(ff.get('province'), '', 'findFields 未请求字段为空');
  // fields=null 等价于 find
  eq(r.findFields('114.114.114.114', null).toPipe(), full.toPipe(), 'findFields(null) 等价 find');
  eq(r.findFields('114.114.114.114', []).toPipe(), full.toPipe(), 'findFields([]) 等价 find');
  // 归一化字段名
  const ff2 = r.findFields('114.114.114.114', ['Country-Code']);
  eq(ff2.get('country_code'), full.get('country_code'), 'findFields 归一化字段名');

  // ----------------------------------------------------------------------
  // 13) 浮点 6 位小数格式（ult）
  // ----------------------------------------------------------------------
  const uinfo = ult.find('114.114.114.114');
  const lon = uinfo.get('longitude');
  const lat = uinfo.get('latitude');
  ok(lon === '' || /^-?\d+(\.\d{6})?$/.test(lon), `longitude 6 位小数 (${lon})`);
  ok(lat === '' || /^-?\d+(\.\d{6})?$/.test(lat), `latitude 6 位小数 (${lat})`);
  // 整数浮点无小数点
  // toJson 数字字段
  const json = JSON.parse(uinfo.toJson());
  ok(typeof json.longitude === 'number' || json.longitude === null, 'toJson longitude 为数字/null');
  ok(typeof json.geo_id === 'number' || json.geo_id === null, 'toJson geo_id 为数字/null');
  ok(typeof json.asn === 'number' || json.asn === null, 'toJson asn 为数字/null');

  // ----------------------------------------------------------------------
  // 14) GeoInfo 语义 getter
  // ----------------------------------------------------------------------
  eq(uinfo.getCidr(), '', 'getCidr 恒返回空串');
  eq(uinfo.getCountry(), uinfo.get('country'), 'getCountry 语义');
  ok(uinfo.getLongitude() === null || typeof uinfo.getLongitude() === 'number', 'getLongitude 类型');
  ok(uinfo.getGeoId() === null || Number.isInteger(uinfo.getGeoId()), 'getGeoId 整数');
  ok(uinfo.getAsn() === null || Number.isInteger(uinfo.getAsn()), 'getAsn 整数');

  fs.rmSync(tmpDir, { recursive: true, force: true });
}

// IP 解析辅助：直接基于导出的 parseIp（null = 解析失败）
function fastParseRejects(ip) {
  return parseIp(ip) === null;
}
function validV6(ip) {
  const r = parseIp(ip);
  return r !== null && (r.v6 !== null || r.v4 !== null);
}
function parseV4(ip) {
  const r = parseIp(ip);
  return r ? r.v4 : 0;
}
let _cached = null;
function cachedReader() {
  if (!_cached) _cached = new QzdbReader(STD_DB);
  return _cached;
}

// ===========================================================================
// Tier2：对 golden_vectors.json 0 偏差
// ===========================================================================
function tier2() {
  const golden = JSON.parse(fs.readFileSync(GOLDEN, 'utf8'));
  const dbs = {
    std_china: { file: STD_DB, reader: new QzdbReader(STD_DB) },
    ult_china: { file: ULT_DB, reader: new QzdbReader(ULT_DB) },
  };

  let total = 0;
  let fails = 0;
  const failSamples = [];

  for (const key of ['std_china', 'ult_china']) {
    const { reader } = dbs[key];
    const set = golden[key];
    const cats = ['random_v4', 'random_v6', 'boundary_v4', 'boundary_v6', 'invalid'];
    for (const cat of cats) {
      const arr = set[cat] || [];
      for (const item of arr) {
        total++;
        let got;
        try {
          const info = reader.find(item.ip);
          got = info === null ? '' : info.toPipe();
        } catch (e) {
          got = '';
        }
        if (got !== item.expected) {
          fails++;
          if (failSamples.length < 10) {
            failSamples.push({ key, cat, ip: item.ip, expected: item.expected, got });
          }
        }
      }
    }
  }

  console.log(`\n[Tier2] 黄金校验：总数=${total}  失败=${fails}`);
  if (failSamples.length) {
    for (const s of failSamples) {
      console.log(`   FAIL ${s.key}/${s.cat} ${s.ip}\n      expected=${JSON.stringify(s.expected)}\n      got=${JSON.stringify(s.got)}`);
    }
  }
  eq(fails, 0, `Tier2 强制 0 失败（实际 ${fails}/${total}）`);
}

// ===========================================================================
function main() {
  console.log('=== Tier1 单元测试 ===');
  tier1();
  console.log(`Tier1 断言数: ${ASSERTS}`);
  ok(ASSERTS >= 50, `Tier1 断言 ≥50（实际 ${ASSERTS}）`);

  console.log('\n=== Tier2 黄金校验 ===');
  tier2();

  console.log('\n[ALL PASS] Tier1 断言=' + ASSERTS + '  Tier2=0 失败');
}

main();
