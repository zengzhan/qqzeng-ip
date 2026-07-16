# encoding: utf-8
# zhouziqing / 233355@gmail.com
# https://github.com/u0x01/logstash-filter-zengip
# 2016/12/20

class ZengIP
  def initialize(db_path)
    @dat = File.binread(db_path).unpack('C*')
    @first_index_offset = b_to_i @dat[0], @dat[1], @dat[2], @dat[3]
    prefix_begin_offset = b_to_i @dat[8], @dat[9], @dat[10], @dat[11]
    prefix_end_offset = b_to_i @dat[12], @dat[13], @dat[14], @dat[15]
    prefix_count =(prefix_end_offset-prefix_begin_offset)/9 + 1 # 前缀区块每组

    # 初始化前缀对应索引区区间
    @prefix_map = {}
    index_buffer = @dat[prefix_begin_offset...(prefix_end_offset+9)]
    prefix_count.times do |k|
      i = k * 9
      prefix = (index_buffer[i] & 0xFF).to_i
      prefix_index = {
          begin: b_to_i(index_buffer[i+1], index_buffer[i+2], index_buffer[i+3], index_buffer[i+4]),
          end: b_to_i(index_buffer[i+5], index_buffer[i+6], index_buffer[i+7], index_buffer[i+8])
      }
      @prefix_map[prefix] = prefix_index
    end
  end

  def info(ip)
    ips = ip.split('.')
    prefix = @prefix_map[ips[0].to_i]
    return nil if prefix.nil?
    int_ip = cidr_to_i ips

    low, high = prefix[:begin], prefix[:end]

    if low == high
      my_index = low
    else
      my_index = binary_search low, high, int_ip
    end

    ip_index = index my_index

    return nil unless ip_index[:begin] <= int_ip && ip_index[:end] >= int_ip
    bytes = @dat[ip_index[:local_offset]...ip_index[:local_offset]+ip_index[:local_length]]
    info = bytes.pack('C*').force_encoding('utf-8').split('|')
    {
        continent: info[0],
        country: info[1],
        province: info[2],
        city: info[3],
        district: info[4],
        isp: info[5],
        dma_code: info[6],
        country_en: info[7],
        country_code: info[8],
        longitude: info[9].to_f,
        latitude: info[10].to_f,
    }
  end


  private
  # 字节转整形
  def b_to_i(a, b, c, d)
    ((a & 0xFF) | ((b << 8) & 0xFF00) | ((c << 16) & 0xFF0000) | ((d << 24) & 0xFF000000)).to_i
  end

  def b3_to_i(a, b, c)
    (a & 0xFF) | ((b << 8) & 0xFF00) | ((c << 16) & 0xFF0000).to_i
  end

  # 二分逼近算法
  def binary_search(low, high, k)
    m = 0
    while low <= high
      mid = (low + high) / 2
      end_ip_num = end_ip mid
      if end_ip_num > k
        m = mid
        break if mid == 0 # 防止溢出
        high = mid - 1
      else
        low = mid + 1
      end
    end
    m
  end

  def end_ip(left)
    left_offset = @first_index_offset + left*12
    b_to_i @dat[4+left_offset], @dat[5+left_offset], @dat[6+left_offset], @dat[7+left_offset]
  end

  def cidr_to_i(cidr)
    ret = cidr[3].to_i
    ret += cidr[2].to_i << 8
    ret += cidr[1].to_i << 16
    ret += cidr[0].to_i << 24
    ret
  end

  def index(left)
    left_offset = @first_index_offset + left * 12
    {
        begin: b_to_i(@dat[left_offset], @dat[1+left_offset], @dat[2+left_offset], @dat[3+left_offset]),
        end: b_to_i(@dat[4+left_offset], @dat[5+left_offset], @dat[6+left_offset], @dat[7+left_offset]),
        local_offset: b3_to_i(@dat[8+left_offset], @dat[9+left_offset], @dat[10+left_offset]),
        local_length: @dat[11+left_offset]
    }
  end
end
