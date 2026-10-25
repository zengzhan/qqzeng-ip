/**
 * qqzeng-ip 6.0 (.db) TypeScript 声明 (CJS + ESM 双入口)
 */
declare class IpDbSearch {
  constructor(dbPath?: string);

  static getInstance(): IpDbSearch;
  static fromBytes(buf: Uint8Array): IpDbSearch;
  static fastParseIp(ip: string): number;
  static findDbPath(): string | null;

  find(ip: string): string;
  findUint(ipInt: number): string;

  readonly nodeCount: number;
  readonly stringCount: number;
}

export = IpDbSearch;
