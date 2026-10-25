/**
 * qqzeng-ip 2.0 (.dat) TypeScript 类型声明。
 */

declare class IpSearch {
  constructor(dbPath?: string);
  /** 从内存字节流加载 */
  static fromBytes(bytes: Buffer | Uint8Array): IpSearch;
  /** 自动探测数据库路径 */
  static findDbPath(startDir?: string): string | null;
  /** 严格 IPv4 解析,非法返回 -1 */
  static fastParseIp(ip: string): number;
  /** IP 查询: 未命中/非法返回 "" */
  find(ip: string): string;
  /** 兼容旧版命名 */
  query(ip: string): string;
  /** 整数(uint32)查询: 未命中返回 "" */
  findUint(val: number): string;
}

declare namespace IpSearchModule {
  function load(file?: string): void;
  function query(ip: string): string;
}

export = IpSearchModule;
export const IpSearch: typeof IpSearch;
