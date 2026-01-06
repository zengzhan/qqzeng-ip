# qqzeng-ip 6.0 API 参考文档

## 概述

本文档详细介绍qqzeng-ip 6.0在8种编程语言中的API使用方法。所有语言版本都遵循相同的接口设计，确保跨语言的一致性。

## C 语言 API

### 初始化

#### `ipdb_init()`
```c
ipdb_search_t* ipdb_init(const char *db_path);
```

**参数**:
- `db_path`: 数据库文件路径

**返回值**:
- 成功: 返回`ipdb_search_t*`指针
- 失败: 返回`NULL`

**示例**:
```c
ipdb_search_t *ctx = ipdb_init("qqzeng-ip-6.0-global.db");
if (!ctx) {
    printf("Failed to load database\n");
    return -1;
}
```

### 查询

#### `ipdb_find()`
```c
const char* ipdb_find(ipdb_search_t *ctx, const char *ip);
```

**参数**:
- `ctx`: 数据库上下文指针
- `ip`: IP地址字符串 (如 "8.8.8.8")

**返回值**:
- 成功: 返回地理位置字符串指针
- 失败: 返回空字符串""

**示例**:
```c
const char* result = ipdb_find(ctx, "8.8.8.8");
printf("Location: %s\n", result);
```

#### `ipdb_find_uint()`
```c
const char* ipdb_find_uint(ipdb_search_t *ctx, uint32_t ip_int);
```

**参数**:
- `ctx`: 数据库上下文指针
- `ip_int`: 32位无符号IP整数

**返回值**:
- 成功: 返回地理位置字符串指针
- 失败: 返回空字符串""

**示例**:
```c
uint32_t ip = 0x08080808;  // 8.8.8.8
const char* result = ipdb_find_uint(ctx, ip);
printf("Location: %s\n", result);
```

### 资源释放

#### `ipdb_free()`
```c
void ipdb_free(ipdb_search_t *ctx);
```

**参数**:
- `ctx`: 数据库上下文指针

**示例**:
```c
ipdb_free(ctx);
```

## Rust API

### 初始化

#### `IpDbSearch::instance()`
```rust
pub fn instance() -> &'static IpDbSearch
```

**返回值**: 返回全局单例引用

**示例**:
```rust
use qqzeng_ip::IpDbSearch;

let searcher = IpDbSearch::instance();
```

### 查询

#### `find()`
```rust
pub fn find(&self, ip: &str) -> &str
```

**参数**:
- `ip`: IP地址字符串切片

**返回值**: 返回地理位置字符串切片

**示例**:
```rust
let location = searcher.find("8.8.8.8");
println!("Location: {}", location);
```

#### `find_uint()`
```rust
pub fn find_uint(&self, prefix: u16, suffix: u16) -> &str
```

**参数**:
- `prefix`: IP地址前16位
- `suffix`: IP地址后16位

**返回值**: 返回地理位置字符串切片

**示例**:
```rust
let location = searcher.find_uint(0x0808, 0x0808);
println!("Location: {}", location);
```

## Go API

### 初始化

#### `ipdb.Instance()`
```go
func Instance() (*IpDbSearch, error)
```

**返回值**:
- 成功: 返回`*IpDbSearch`和`nil`
- 失败: 返回`nil`和错误信息

**示例**:
```go
searcher, err := ipdb.Instance()
if err != nil {
    log.Fatal(err)
}
```

### 查询

#### `Find()`
```go
func (searcher *IpDbSearch) Find(ip string) string
```

**参数**:
- `ip`: IP地址字符串

**返回值**: 返回地理位置字符串

**示例**:
```go
location := searcher.Find("8.8.8.8")
fmt.Printf("Location: %s\n", location)
```

#### `FindUint()`
```go
func (searcher *IpDbSearch) FindUint(prefix, suffix uint16) string
```

**参数**:
- `prefix`: IP地址前16位
- `suffix`: IP地址后16位

**返回值**: 返回地理位置字符串

**示例**:
```go
location := searcher.FindUint(0x0808, 0x0808)
fmt.Printf("Location: %s\n", location)
```

## Java API

### 初始化

#### `IpDbSearch.getInstance()`
```java
public static IpDbSearch getInstance()
```

**返回值**: 返回全局单例实例

**示例**:
```java
import com.qqzeng.ip.IpDbSearch;

IpDbSearch searcher = IpDbSearch.getInstance();
```

### 查询

#### `find()`
```java
public String find(String ip)
```

**参数**:
- `ip`: IP地址字符串

**返回值**: 返回地理位置字符串

**示例**:
```java
String location = searcher.find("8.8.8.8");
System.out.println("Location: " + location);
```

## Node.js API

### 初始化

#### `IpDbSearch.getInstance()`
```javascript
static getInstance(): IpDbSearch
```

**返回值**: 返回全局单例实例

**示例**:
```javascript
const IpDbSearch = require('./lib/IpDbSearch');
const searcher = IpDbSearch.getInstance();
```

### 查询

#### `find()`
```javascript
find(ip: string): string
```

**参数**:
- `ip`: IP地址字符串

**返回值**: 返回地理位置字符串

**示例**:
```javascript
const location = searcher.find("8.8.8.8");
console.log(`Location: ${location}`);
```

#### `findUint()`
```javascript
findUint(ip: number): string
```

**参数**:
- `ip`: 32位无符号IP整数

**返回值**: 返回地理位置字符串

**示例**:
```javascript
const location = searcher.findUint(0x08080808);
console.log(`Location: ${location}`);
```

## .NET API

### 初始化

#### `IpDbSearch.Instance`
```csharp
public static IpDbSearch Instance { get; }
```

**返回值**: 返回全局单例实例

**示例**:
```csharp
using qqzengIp;

var searcher = IpDbSearch.Instance;
```

### 查询

#### `Find()`
```csharp
public string Find(string ip)
```

**参数**:
- `ip`: IP地址字符串

**返回值**: 返回地理位置字符串

**示例**:
```csharp
string location = searcher.Find("8.8.8.8");
Console.WriteLine($"Location: {location}");
```

#### `Find()`
```csharp
public string Find(uint ip)
```

**参数**:
- `ip`: 32位无符号IP整数

**返回值**: 返回地理位置字符串

**示例**:
```csharp
string location = searcher.Find(0x08080808);
Console.WriteLine($"Location: {location}");
```

## PHP API

### 初始化

#### `IpDbSearch::getInstance()`
```php
public static function getInstance(): IpDbSearch
```

**返回值**: 返回全局单例实例

**示例**:
```php
use Qqzeng\Ip\IpDbSearch;

$searcher = IpDbSearch::getInstance();
```

### 查询

#### `find()`
```php
public function find(string $ip): string
```

**参数**:
- `$ip`: IP地址字符串

**返回值**: 返回地理位置字符串

**示例**:
```php
$location = $searcher->find("8.8.8.8");
echo "Location: " . $location . "\n";
```

## Python API

### 初始化

#### `__init__()`
```python
def __init__(self)
```

**示例**:
```python
from qqzeng_ip.ipdb import IpDbSearch

searcher = IpDbSearch()
```

### 查询

#### `find()`
```python
def find(self, ip: str) -> str
```

**参数**:
- `ip`: IP地址字符串

**返回值**: 返回地理位置字符串

**示例**:
```python
location = searcher.find("8.8.8.8")
print(f"Location: {location}")
```

## 返回数据格式

所有API都返回相同格式的地理位置字符串：

### 标准格式
```
大洲|国家|省份|城市|区县|ISP|行政区码|国家英文名|ISO代码|经度|纬度
```

### 字段说明

| 字段 | 说明 | 示例 |
|------|------|------|
| 大洲 | 地理大洲 | 亚洲 |
| 国家 | 国家或地区名称 | 中国 |
| 省份 | 省级行政区 | 广东省 |
| 城市 | 城市名称 | 深圳市 |
| 区县 | 区县级名称 | 南山区 |
| ISP | 互联网服务提供商 | 中国电信 |
| 行政区码 | 中国行政区划代码 | 440300 |
| 国家英文名 | 国家英文名称 | China |
| ISO代码 | ISO 3166-1国家代码 | CN |
| 经度 | 地理经度 | 114.0579 |
| 纬度 | 地理纬度 | 22.5431 |

### 示例返回

```
亚洲|中国|广东省|深圳市|南山区|中国电信|440300|China|CN|114.0579|22.5431
```

## 错误处理

### 常见错误情况

1. **无效IP地址**：返回空字符串`""`
2. **数据库文件不存在**：初始化时抛出异常
3. **数据库文件损坏**：初始化时抛出异常
4. **IPv6地址**：当前版本不支持，返回空字符串

### 推荐处理方式

```c
// C语言示例
const char* result = ipdb_find(ctx, ip);
if (strlen(result) == 0) {
    // 处理查询失败
    return handle_error(ip);
}
```

```java
// Java示例
String result = searcher.find(ip);
if (result.isEmpty()) {
    // 处理查询失败
    return handleError(ip);
}
```

## 性能优化

### 高性能查询

1. **优先使用整数查询接口**：
   - C: `ipdb_find_uint()`
   - Go: `FindUint()`
   - Rust: `find_uint()`

2. **避免重复初始化**：使用单例模式

3. **批量查询优化**：
   ```c
   // 预转换IP为整数
   uint32_t ip_int = inet_addr(ip_str);
   const char* result = ipdb_find_uint(ctx, ip_int);
   ```

### 内存管理

1. **C语言**：
   - 不要释放返回的字符串指针
   - 使用`ipdb_free()`释放资源

2. **托管语言**：
   - 自动内存管理，无需手动释放

## 最佳实践

### 线程安全

所有语言实现都是线程安全的：

```java
// Java示例 - 多线程使用
ExecutorService executor = Executors.newFixedThreadPool(10);
for (int i = 0; i < 100; i++) {
    executor.submit(() -> {
        String location = searcher.find("8.8.8.8");
        // 处理结果
    });
}
```

```go
// Go示例 - 协程并发
var wg sync.WaitGroup
for i := 0; i < 100; i++ {
    wg.Add(1)
    go func() {
        defer wg.Done()
        location := searcher.Find("8.8.8.8")
        // 处理结果
    }()
}
wg.Wait()
```

### 错误处理

```python
# Python示例
try:
    searcher = IpDbSearch()
except Exception as e:
    print(f"Failed to initialize: {e}")
    return

location = searcher.find(ip)
if not location:
    print(f"IP not found: {ip}")
else:
    print(f"Location: {location}")
```

## 版本兼容性

### API版本

- **v6.0**: 当前版本，稳定的API
- **v5.x**: 旧版本，API不兼容

### 数据库版本

- **v6.0**: 当前数据库格式
- **v5.x**: 旧格式，不兼容

## 故障排除

### 常见问题

1. **编译错误**：
   - 确保使用支持的编译器版本
   - 检查依赖库是否安装

2. **运行时错误**：
   - 检查数据库文件路径
   - 确认文件权限

3. **性能问题**：
   - 使用整数查询接口
   - 检查系统缓存设置

### 调试工具

```c
// C语言调试宏
#define DEBUG_IP_SEARCH 1

#if DEBUG_IP_SEARCH
printf("IP: %s, Result: %s\n", ip, result);
#endif
```

---

**文档版本**: 6.0  
**最后更新**: 2026-01-06  
**维护者**: qqzeng-ip团队