using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace qqzeng_phone_db
{
    public sealed class  PhoneSearch6Db
    {
        private static readonly Lazy<PhoneSearch6Db> _lazy = new(() => new PhoneSearch6Db());
        public static PhoneSearch6Db Instance => _lazy.Value;

        // 常量定义（根据文件格式规范）
        private const int HeaderSize = 32;                  // 8个uint的头部
        private const int PrefixCount = 200;                 // 电话号码前缀总数（0-199）
        private const int BitmapPopCountOffset = 0x4E2;      // 位图统计信息偏移量

        // 字段定义
        private byte[] _data = Array.Empty<byte>();          // 数据库原始数据
        private string[] _regionIsps = Array.Empty<string>(); // 地区-运营商组合缓存
        private readonly (int BitmapOffset, int DataOffset)[] _index
            = new (int, int)[PrefixCount];                   // 前缀索引表


        private PhoneSearch6Db() => LoadDatabase();

        /// <summary>
        /// 加载并解析数据
        /// </summary>
        /// <exception cref="InvalidDataException">文件格式异常</exception>
        public void LoadDatabase()
        {
            var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qqzeng-phone-6.0.db");
            if (!File.Exists(filePath)) throw new FileNotFoundException("Database file not found", filePath);
            try
            {
                _data = File.ReadAllBytes(filePath);
                var span = _data.AsSpan();

                // 解析头部（小端序）
                var header = new uint[8];
                for (int i = 0; i < header.Length; i++)
                {
                    header[i] = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i * 4, 4));
                }

                // 解析地区与运营商表
                int regionsStart = HeaderSize;
                int ispsStart = regionsStart + (int)header[1];
                int indexStart = ispsStart + (int)header[2];

                string[] regions = Encoding.UTF8.GetString(span.Slice(regionsStart, (int)header[1])).Split('&');
                string[] isps = Encoding.UTF8.GetString(span.Slice(ispsStart, (int)header[2])).Split('&');

                // 构建地区-运营商组合
                _regionIsps = new string[header[4]];
                int entryOffset = (int)header[3];
                for (int i = 0; i < _regionIsps.Length; i++)
                {
                    ushort entry = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(entryOffset + i * 2, 2));
                    _regionIsps[i] = $"{regions[entry >> 5]}|{isps[entry & 0x1F]}";
                }

                // 构建前缀索引表
                int pos = indexStart;
                for (int i = 0; i < PrefixCount; i++)
                {

                    uint prefix = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(pos, 4));
                    if (prefix == i)
                    {
                        _index[i] = (
                            BitmapOffset: BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 4, 4)),
                            DataOffset: BinaryPrimitives.ReadInt32LittleEndian(span.Slice(pos + 8, 4))
                            );
                        pos += 12;
                    }
                    else
                    {
                        _index[i] = (0, 0);
                    }

                }
            }
            catch (Exception ex) when (ex is not FileNotFoundException)
            {
                throw new InvalidDataException("Invalid database format", ex);
            }

        }

        /// <summary>
        /// 查询电话号码归属地信息
        /// </summary>
        /// <param name="phone">7位数字电话号码</param>
        /// <returns>地区|运营商 组合字符串，未找到时返回null</returns>
        /// <exception cref="ArgumentException">无效电话号码格式</exception>
        public string? Query(ReadOnlySpan<char> phone)
        {          

            // 解析前缀和后四位
            int prefix = ParsePhoneSegment(phone[..3]);
            int subNum = ParsePhoneSegment(phone.Slice(3, 4));

          
            // 前缀有效性检查
            if (prefix is < 0 or >= PrefixCount) return null;

            // 获取索引条目
            var (bitmapOffset, dataOffset) = _index[prefix];
            if (bitmapOffset == 0 || dataOffset == 0) return null;

            // 位图检查
            Span<byte> dataSpan = _data.AsSpan();
            int byteIndex = subNum >> 3;        // 等价于 subNum / 8
            int bitIndex = subNum & 0b0111; // 等价于 subNum % 8

            if (bitmapOffset + byteIndex >= dataSpan.Length) return null;

            byte bitmap = dataSpan[bitmapOffset + byteIndex];
            if ((bitmap & (1 << bitIndex)) == 0) return null;

            // 计算有效数据位置
            int popCountOffset = bitmapOffset + BitmapPopCountOffset + (byteIndex << 1);
           
            int preCount = BinaryPrimitives.ReadUInt16LittleEndian(dataSpan.Slice(popCountOffset, 2));
            int localCount = BitOperations.PopCount((uint)(bitmap & ((1 << bitIndex) - 1)));
          

            // 定位最终数据
            int dataPos = dataOffset + ((preCount + localCount) << 1);
            ushort entry = BinaryPrimitives.ReadUInt16LittleEndian(dataSpan.Slice(dataPos, 2));
            return entry < _regionIsps.Length ? _regionIsps[entry] : null;
        }

        /// <summary>
        /// 将电话号码段转换为数字
        /// </summary>
        private static int ParsePhoneSegment(ReadOnlySpan<char> segment)
        {
            int result = 0;
            foreach (char c in segment)
            {
                result = result * 10 + (c - '0');
            }
            return result;
        }
    }
    /*
     *   var local = PhoneSearch6Db.Instance.Query(phone);
     *   return 省份|城市|邮编|区号|行政区划代码|运营商
     *   
    查询 qqzeng-phone-6.0.db 7598万 ->1.484秒 每秒5119.946091644205万次
    查询 qqzeng-phone-6.0.db 6380万 ->1.075秒 每秒5934.883720930233万次
    查询 qqzeng-phone-6.0.db 6438万 ->1.102秒 每秒5842.105263157894万次
    查询 qqzeng-phone-6.0.db 10904万 ->1.889秒 每秒5772.366331392271万次
    查询 qqzeng-phone-6.0.db 9454万 ->1.584秒 每秒5968.434343434343万次
    查询 qqzeng-phone-6.0.db 9048万 ->1.466秒 每秒6171.896316507504万次
    查询 qqzeng-phone-6.0.db 7598万 ->1.219秒 每秒6232.9778506972925万次
    */


}
