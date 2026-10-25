# encoding: utf-8
# frozen_string_literal: true

#
# qqzeng-ip 2.0 (.dat) 高性能解析 - Ruby 版 (Ruby 2.4+)
#
# 文件结构 (UTF-8, Little-Endian):
#   文件头  16 字节 (4-4-4-4): [索引区首条偏移][索引区末条偏移][前缀区首偏移][前缀区末偏移]
#   内容区  唯一不重复的地区字符串,按 (offset,len) 被索引区引用
#   索引区  12 字节/条 (4-4-3-1): [起始IP][结束IP][地区偏移24b][长度8b]
#   前缀区   9 字节/条 (1-4-4): [前缀0-255][索引区start][索引区end]
#
# 兼容旧版 API:
#   ZengIP.new(db_path).info(ip)   # -> 结构化 Hash 或 nil(未命中)
# 新增统一 API:
#   ZengIP.new(db_path).find(ip)   # -> 原始字符串,未命中/非法返回 ''
#   ZengIP.new(db_path).find_uint(ip_int)
#   ZengIP.new                     # 自动探测默认路径
#   ZengIP.from_bytes(binary)      # 从内存字节流加载
#
# 相比历史版本的修复与优化:
#   1. 修复 binary_search 中 end_ip_num > k 的严重 bug(必须 >=,
#      否则恰好落在区间结束 IP 上时误判未命中)。
#   2. 移除 File.binread.unpack('C*')(27MB 文件转 2700 万元素数组的灾难),
#      直接操作二进制 String(Encoding::ASCII_8BIT) + getbyte 位运算。
#   3. 前缀 Hash 改为固定 256 长度数组,缺失前缀为 nil
#      (修复 0.0.0.0/8 前缀 low=high=0 被判未命中的 bug)。
#   4. split('.') 改为单次扫描 fast_parse_ip 严格校验。
#   5. 索引预解析为紧凑数组 + 字符串池去重常驻。
#

class ZengIP
  DEFAULT_NAMES = ['qqzeng-ip-utf8.dat', 'qqzeng-ip.dat',
                   'qqzeng-ip-china-utf8.dat', 'qqzeng-ip-global-utf8.dat'].freeze
  DEFAULT_DIRS = ['.', 'dada/国内精华版-202610', 'dada/全球旗舰版-202610',
                  '../dada/国内精华版-202610', '../dada/全球旗舰版-202610',
                  '../../dada/国内精华版-202610', '../../dada/全球旗舰版-202610'].freeze

  INFO_KEYS = %i[continent country province city district isp dma_code
                 country_en country_code longitude latitude].freeze

  # 兼容旧版: ZengIP.new(db_path);缺省时自动探测默认路径。
  def initialize(db_path = nil)
    db_path ||= self.class.find_db_path
    raise ArgumentError, 'ZengIP: no loadable .dat found' unless db_path

    data = File.binread(db_path)
    data.force_encoding(Encoding::ASCII_8BIT)
    load(data)
  end

  # 从内存字节流加载。
  def self.from_bytes(binary)
    obj = allocate
    obj.send(:load, binary.dup.force_encoding(Encoding::ASCII_8BIT))
    obj
  end

  def self.find_db_path
    DEFAULT_DIRS.each do |d|
      DEFAULT_NAMES.each do |n|
        p = d == '.' ? n : File.join(d, n)
        return p if File.file?(p)
      end
    end
    nil
  end

  # 兼容旧版入口: 结构化结果,未命中返回 nil。
  def info(ip)
    raw = find(ip)
    return nil if raw.nil? || raw.empty?

    parts = raw.split('|', -1)
    info = INFO_KEYS.each_with_index.map { |k, i| [k, parts[i]] }.to_h
    info[:longitude] = info[:longitude].to_f
    info[:latitude] = info[:latitude].to_f
    info
  end

  # 统一入口: 原始字符串,未命中/非法返回 ''。
  def find(ip)
    v = self.class.fast_parse_ip(ip)
    return '' if v < 0

    find_uint(v)
  end

  # 整数查询(跳过字符串解析,高性能场景)。
  def find_uint(val)
    rng = @pref[(val >> 24) & 0xFF]
    return '' unless rng # 前缀不存在(不能以 high==0 判未命中)

    lo, hi = rng
    if lo == hi
      idx = lo
    else
      # 二分逼近: 最小的 endip >= val(注意: 必须 >= 而不是 >)
      ends = @ends
      idx = 0
      while lo <= hi
        mid = lo + ((hi - lo) >> 1)
        if ends[mid] >= val
          idx = mid
          break if mid.zero?

          hi = mid - 1
        else
          lo = mid + 1
        end
      end
    end
    return '' unless @starts[idx] <= val && @ends[idx] >= val

    @strings[@str_idx[idx]]
  end

  # 严格 IPv4 解析: 每段 1~3 位数字且 <=255,恰好 4 段。非法返回 -1。
  def self.fast_parse_ip(ip)
    return -1 unless ip.is_a?(String)

    val = 0
    result = 0
    shift = 24
    digits = 0
    ip.each_char do |c|
      o = c.ord
      if o >= 48 && o <= 57 # '0'-'9'
        val = val * 10 + (o - 48)
        return -1 if val > 255

        digits += 1
        return -1 if digits > 3 # 拒绝 "0008.8.8.8" 这类超长前导零
      elsif o == 46 # '.'
        return -1 if digits.zero? || shift.zero? # 空段或第 5 段

        result |= val << shift
        val = 0
        digits = 0
        shift -= 8
      else
        return -1
      end
    end
    return -1 if digits.zero? || shift != 0

    result | val
  end

  private

  def getbyte(data, o)
    data.getbyte(o)
  end

  def le32(data, o)
    data.getbyte(o) | (data.getbyte(o + 1) << 8) |
      (data.getbyte(o + 2) << 16) | (data.getbyte(o + 3) << 24)
  end

  def load(data)
    raise ArgumentError, 'database too small' if data.bytesize < 16

    first_index = le32(data, 0)
    last_index = le32(data, 4)
    pref_start = le32(data, 8)
    pref_end = le32(data, 12)

    if first_index < 16 || last_index < first_index ||
       (last_index - first_index) % 12 != 0 ||
       pref_end < pref_start || pref_start < 16 || (pref_end - pref_start) % 9 != 0
      raise ArgumentError, 'bad header'
    end

    record_count = (last_index - first_index) / 12 + 1
    prefix_count = (pref_end - pref_start) / 9 + 1
    if first_index + 12 * record_count > data.bytesize ||
       pref_start + 9 * prefix_count > data.bytesize
      raise ArgumentError, 'header exceeds file size'
    end

    @pref = Array.new(256)
    (0...prefix_count).each do |k|
      o = pref_start + k * 9
      p = data.getbyte(o)
      lo = le32(data, o + 1)
      hi = le32(data, o + 5)
      raise ArgumentError, 'bad prefix range' if lo > hi || hi >= record_count

      @pref[p] = [lo, hi]
    end

    @starts = Array.new(record_count)
    @ends = Array.new(record_count)
    @str_idx = Array.new(record_count)
    seen = {}
    strings = []
    (0...record_count).each do |i|
      o = first_index + i * 12
      off = data.getbyte(o + 8) | (data.getbyte(o + 9) << 8) | (data.getbyte(o + 10) << 16)
      slen = data.getbyte(o + 11)
      if off + slen > data.bytesize
        raise ArgumentError, 'string region out of range'
      end

      @starts[i] = le32(data, o)
      @ends[i] = le32(data, o + 4)
      key = off * 256 + slen
      sid = seen[key]
      unless sid
        sid = strings.length
        strings << data.byteslice(off, slen).force_encoding(Encoding::UTF_8)
        seen[key] = sid
      end
      @str_idx[i] = sid
    end
    @strings = strings
  end
end
