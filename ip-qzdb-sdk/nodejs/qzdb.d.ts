export interface QzdbOpenOptions {
  groupIndex?: number;
  verifyCrc?: boolean;
  warmup?: boolean;
}

export interface ParsedIp {
  readonly v4: number | null;
  readonly v6: Buffer | null;
}

export class QzdbError extends Error {
  readonly code: string;
  static readonly NOT_FOUND: string;
  static readonly CORRUPTED: string;
  static readonly OUT_OF_BOUNDS: string;
  static readonly INVALID_PARAM: string;
  static readonly BAD_HEADER: string;
  static readonly BAD_MAGIC: string;
  static readonly UNSUPPORTED: string;
  constructor(message: string, code?: string);
}

export class UsageType {
  rawValue(): string;
  getDisplayZh(): string;
  getDisplayEn(): string;
  getDescription(): string;
  isKnown(): boolean;
  toString(): string;
  static fromString(raw: string | null | undefined): UsageType;
}

export class GeoInfo {
  readonly [field: string]: unknown;
  get(name: string): string;
  toPipe(): string;
  toPipeString(): string;
  toMap(): Record<string, string>;
  toDict(): Record<string, string>;
  toJson(): string;
  toString(): string;
  fieldNames(): string[];
  values(): string[];
  getCidr(): string;
  getCountry(): string;
  getCountryEn(): string;
  getCountryAlpha2(): string;
  getCountryAlpha3(): string;
  getContinent(): string;
  getContinentEn(): string;
  getCountryCode(): string;
  getProvince(): string;
  getProvinceEn(): string;
  getCity(): string;
  getCityEn(): string;
  getDistrict(): string;
  getTimezone(): string;
  getIsp(): string;
  getIspEn(): string;
  getAsName(): string;
  getAsDomain(): string;
  getCurrencyCode(): string;
  getCurrencyName(): string;
  getPhonePrefix(): string;
  getEmojiFlag(): string;
  getLanguages(): string;
  getGeoId(): number | null;
  getLongitude(): number | null;
  getLatitude(): number | null;
  getAsn(): number | null;
  getUsageType(): UsageType;
}

export class RowIds {
  readonly geoId: number;
  readonly asnId: number;
  readonly usageId: number;
  constructor(geoId: number, asnId: number, usageId: number);
}

export class BatchResult {
  readonly input: string;
  readonly result: GeoInfo | null;
  readonly error: QzdbError | null;
  readonly info: GeoInfo | null;
  isSuccess(): boolean;
  isNotFound(): boolean;
  hasError(): boolean;
}

export class QzdbRegistry {
  static QUARANTINE_CAPACITY: number;
  register(name: string, path: string): QzdbReader;
  registerBuffer(name: string, buffer: Buffer | Uint8Array | ArrayBuffer): QzdbReader;
  get(name: string): QzdbReader | null;
  unregister(name: string): void;
  clear(): void;
  static registerGlobal(name: string, path: string): QzdbReader;
  static registerGlobalBuffer(name: string, buffer: Buffer | Uint8Array | ArrayBuffer): QzdbReader;
  static getGlobal(name: string): QzdbReader | null;
  static unregisterGlobal(name: string): void;
  static clearGlobal(): void;
}

export class ChainedReader {
  static chain(...readers: QzdbReader[]): ChainedReader;
  static chainMerge(...readers: QzdbReader[]): ChainedReader;
  static chainMergeOverride(...readers: QzdbReader[]): ChainedReader;
  find(ipStr: string): GeoInfo | null;
  findUint(ipInt: number): GeoInfo | null;
  findBytes(ipBytes: Buffer | Uint8Array): GeoInfo | null;
  findFields(ipStr: string, fields?: string[] | null): GeoInfo | null;
  findBatch(ips: Iterable<string> | null | undefined): BatchResult[];
  findBatchFields(ips: Iterable<string> | null | undefined, fields?: string[] | null): BatchResult[];
  findStream(ips: Iterable<string> | null | undefined): Generator<BatchResult, void, unknown>;
  editions(): string[];
  scopes(): string[];
  dataMonths(): string[];
  readers(): QzdbReader[];
}

export declare function parseIp(ip: string): ParsedIp | null;
export declare function editionFromMask(mask: number): string;

export declare const EDITION_BY_BIT: readonly string[];
export declare const EDITION_FIELD_NAMES: Readonly<Record<string, readonly string[]>>;
export declare const EDITION_SOURCE_VERSION_MASK: string;
export declare const EDITION_SOURCE_METADATA: string;
export declare const EDITION_SOURCE_INFERRED: string;
export declare const EDITION_SOURCE_UNKNOWN: string;
export declare const FIELD_NAMES_SOURCE_METADATA: string;
export declare const FIELD_NAMES_SOURCE_EDITION: string;
export declare const FIELD_NAMES_SOURCE_SYNTHETIC: string;

export default class QzdbReader {
  constructor(dbPath?: string | null, groupIndex?: number, verifyCrc?: boolean, warmup?: boolean);
  static open(path: string, options?: QzdbOpenOptions): QzdbReader;
  static openBuffer(buffer: Buffer | Uint8Array | ArrayBuffer, options?: QzdbOpenOptions): QzdbReader;
  static Builder: {
    new (source: string | Buffer | Uint8Array | ArrayBuffer): {
      groupIndex(i: number): any;
      verifyCrc(b: boolean): any;
      build(): QzdbReader;
    };
  };
  static GeoInfo: typeof GeoInfo;
  static UsageType: typeof UsageType;
  static RowIds: typeof RowIds;
  static BatchResult: typeof BatchResult;
  static QzdbRegistry: typeof QzdbRegistry;
  static ChainedReader: typeof ChainedReader;
  static QzdbError: typeof QzdbError;
  static Error: typeof QzdbError;
  static parseIp: typeof parseIp;

  load(dbPath: string, verifyCrc?: boolean | null): this;
  loadBuffer(bytes: Buffer | Uint8Array | ArrayBuffer, verifyCrc?: boolean | null): this;
  warmup(): void;
  reload(dbPath: string): this;
  reloadBuffer(bytes: Buffer | Uint8Array | ArrayBuffer): this;
  close(): void;

  find(ipStr: string): GeoInfo | null;
  findUint(ipInt: number): GeoInfo | null;
  findV6Uint(ipInt: bigint): GeoInfo | null;
  findV6(high: bigint, low: bigint): GeoInfo | null;
  findBytes(ipBytes: Buffer | Uint8Array): GeoInfo | null;
  findFields(ipStr: string, fieldNames?: string[] | null): GeoInfo | null;
  findStr(ipStr: string): string;
  findBatch(ips: Iterable<string> | null | undefined): BatchResult[];
  findBatchFields(ips: Iterable<string> | null | undefined, fields?: string[] | null): BatchResult[];
  findIter(ips: Iterable<string> | null | undefined): Generator<BatchResult, void, unknown>;
  findStream(ips: Iterable<string> | null | undefined): Generator<BatchResult, void, unknown>;

  lookupRowId(ipStr: string): number;
  lookupRowIdUint(ipInt: number): number;
  lookupRowIdV6(ipInt: bigint): number;
  lookupRowIdBytes(ipBytes: Buffer | Uint8Array): number;
  lookupIds(rowId: number): RowIds | null;

  lookupCidr(ipStr: string): string | null;
  lookupCidrUint(ipInt: number): string | null;
  lookupCidrBytes(ipBytes: Buffer | Uint8Array): string | null;

  getVersion(): string;
  getDataMonth(): string;
  getEdition(): string;
  getVersionMask(): number;
  getEditionSource(): string;
  getFieldNamesSource(): string;
  getScope(): string;
  getBuildTime(): string;
  getDescription(): string;
  getFileHash(): string;
  getFieldNames(): string[];
  hasField(name: string): boolean;
  verifyCrc(): boolean;
  getGroupCount(): number;
  getPoolCount(): number;
}
