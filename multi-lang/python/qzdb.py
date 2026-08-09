import ipaddress
import mmap
import os
import struct
import threading
import zlib
from collections import namedtuple
from enum import Enum

SENTINEL = 0x80000000
SENTINEL_MASK_24 = 0x7FFFFF
SENTINEL_MASK_31 = 0x7FFFFFFF
MAX_TRIE_WALK_STEPS = 1000
MAX_POOL_COUNT = 1 << 26
FLOAT_FIELDS = frozenset(['longitude', 'latitude'])

# toJson numeric fields (API contract §6): output as JSON numbers, not strings.
NUMERIC_FIELDS = frozenset(['longitude', 'latitude', 'asn', 'geo_id'])

# ---------------------------------------------------------------------------
# Edition registry (FORMAT §3.1 / §10.3).
#
# The file is self-describing: `Header.VersionMask` (offset 6) and each
# `GROUP_SCHEMA.groupId` carry a one-hot edition bitmask. That bitmask — not
# the field count — is the authoritative edition signal. EDITION_BY_BIT is the
# spec's bit -> name registry; adding a future edition means appending one bit
# here and one row to EDITION_FIELD_NAMES, with no parser changes anywhere.
# ---------------------------------------------------------------------------
EDITION_BY_BIT = ('std', 'asn', 'pro', 'max', 'ult')  # bit0..bit4

# Canonical field order per edition (FORMAT appendix 1). Used ONLY when a file
# carries no Metadata `field_names`; never overrides the file's own names.
EDITION_FIELD_NAMES = {
    'std': ('continent', 'country_code', 'country', 'province', 'city', 'isp'),
    'asn': ('continent', 'country_code', 'country', 'isp', 'asn', 'as_name',
            'as_domain', 'usage_type'),
    'pro': ('continent', 'country_code', 'country', 'province', 'city', 'district',
            'geo_id', 'longitude', 'latitude', 'timezone', 'isp'),
    'max': ('continent', 'country_code', 'country', 'province', 'city', 'district',
            'geo_id', 'longitude', 'latitude', 'timezone', 'isp', 'asn', 'as_name',
            'as_domain', 'usage_type'),
    'ult': ('continent', 'continent_en', 'country_code', 'country_alpha3', 'country',
            'country_en', 'province', 'province_en', 'city', 'city_en', 'district',
            'district_en', 'geo_id', 'longitude', 'latitude', 'timezone', 'languages',
            'currency_code', 'phone_prefix', 'emoji_flag', 'isp', 'asn', 'as_name',
            'as_domain', 'usage_type'),
}

# Provenance markers. Identical string values in all 8 SDKs so cross-language
# verification can compare them directly.
EDITION_SOURCE_VERSION_MASK = 'version_mask'  # from VersionMask/groupId (authoritative)
EDITION_SOURCE_METADATA = 'metadata'          # from Metadata primary_version/version_list
EDITION_SOURCE_INFERRED = 'inferred'          # last resort: unique field-count match
EDITION_SOURCE_UNKNOWN = 'unknown'            # genuinely undeterminable

FIELD_NAMES_SOURCE_METADATA = 'metadata'      # file's own Metadata field_names
FIELD_NAMES_SOURCE_EDITION = 'edition'        # canonical table for a known edition
FIELD_NAMES_SOURCE_SYNTHETIC = 'synthetic'    # field_0..field_N-1 placeholders

# Reverse index: field count -> edition, only when the count is unambiguous.
_EDITION_BY_FIELD_COUNT = {}
for _ed, _names in EDITION_FIELD_NAMES.items():
    _EDITION_BY_FIELD_COUNT[len(_names)] = None if len(_names) in _EDITION_BY_FIELD_COUNT else _ed
del _ed, _names


def edition_from_mask(mask):
    """Resolve a one-hot edition bitmask to its name, or '' if not one-hot."""
    if mask <= 0 or (mask & (mask - 1)) != 0:
        return ''  # zero, or more than one bit set -> not a single edition
    bit = mask.bit_length() - 1
    return EDITION_BY_BIT[bit] if bit < len(EDITION_BY_BIT) else ''


def _norm_key(name):
    """Normalize a field name for case/underscore/hyphen-insensitive lookup.

    Matches the cross-language rule (API contract §6): lowercase and drop all
    ``_`` and ``-`` so ``country_code`` == ``countryCode`` == ``COUNTRY_CODE``
    == ``Country-Code``.
    """
    if not isinstance(name, str):
        return ''
    out = []
    for ch in name:
        if ch == '_' or ch == '-':
            continue
        out.append(ch.lower())
    return ''.join(out)


# ── strict IP parsing (SEC-05 / CODE-03) ───────────────────────────

# bytearray[256]: index-ASCII → hex value (0xFF = invalid)
_HEX = bytearray([0xFF] * 256)
for _i in range(10):
    _HEX[48 + _i] = _i
for _i in range(6):
    _HEX[97 + _i] = 10 + _i
    _HEX[65 + _i] = 10 + _i


def _fast_parse_ip(s):
    """Parse IP string with strict validation (SEC-05).
    Returns (v4_int, None) for IPv4 or (None, v6_bytes) for IPv6.
    Returns None for invalid input. Fail-closed on any whitespace (no silent trim):
    embedded whitespace is rejected by the digit/hex validation below, never trimmed.
    Max length 45.
    """
    if not isinstance(s, str):
        return None
    n = len(s)
    if n == 0 or n > 45:
        return None
    if ':' not in s:
        return _fast_parse_ipv4(s)
    return _fast_parse_ipv6(s)


def _fast_parse_ipv4(s):
    """Strict IPv4 parse. Returns (uint32, None) or None.
    Exactly 4 dot-separated segments, no leading zeros, each 0-255,
    no trailing dot, no empty segments, no port suffix. ASCII-only digits.
    """
    if s[-1] == '.':
        return None
    parts = s.split('.')
    if len(parts) != 4:
        return None
    ip = 0
    for p in parts:
        pl = len(p)
        if pl == 0 or pl > 3 or (pl > 1 and p[0] == '0') or not p.isascii() or not p.isdigit():
            return None
        v = int(p)
        if v > 255:
            return None
        ip = (ip << 8) | v
    return (ip, None)


def _fast_parse_ipv6(s):
    """Strict IPv6 parse. Returns (None, bytes(16)) or None.
    Max one '::', ≤8 groups, reject %zone, allow last 32 bits as
    IPv4 dotted decimal.  ::ffff:a.b.c.d → extracted as V4.
    """
    if '%' in s:
        return None
    dc = s.find('::')
    if dc >= 0:
        if s.find('::', dc + 2) >= 0:
            return None
        lft = s[:dc]
        rgt = s[dc + 2:]
    else:
        lft = s
        rgt = ''
    lg = lft.split(':') if lft else []
    rg = rgt.split(':') if rgt else []
    if lg == ['']:
        lg = []
    if rg == ['']:
        rg = []
    for g in lg:
        if not g:
            return None
    for g in rg:
        if not g:
            return None
    allg = lg + rg
    has_v4 = False
    v4_int = 0
    if allg and '.' in allg[-1]:
        vr = _fast_parse_ipv4(allg[-1])
        if vr is None:
            return None
        v4_int = vr[0]
        has_v4 = True
        allg = allg[:-1]
        # Pop from rg/lg too so the hex-iteration loop doesn't see the V4 group
        if rg:
            rg.pop()
        else:
            lg.pop()
    ng = len(allg)
    v4_slots = 2 if has_v4 else 0
    if dc >= 0:
        if ng + v4_slots > 7:
            return None
    else:
        if ng + v4_slots != 8:
            return None
    for g in allg:
        gl = len(g)
        if gl == 0 or gl > 4:
            return None
        for c in g:
            # 仅接受 0-9 a-f A-F；拒绝 + - _ 0x 空白等（与 Rust 逐字符白名单对齐）
            if _HEX[ord(c)] > 15:
                return None
    zeros = 8 - ng - v4_slots
    if zeros < 0:
        return None
    buf = bytearray(16)
    off = 0
    for g in lg:
        v = 0
        for c in g:
            v = (v << 4) | _HEX[ord(c)]
        buf[off] = v >> 8
        buf[off + 1] = v & 0xFF
        off += 2
    off += zeros * 2
    for g in rg:
        v = 0
        for c in g:
            v = (v << 4) | _HEX[ord(c)]
        buf[off] = v >> 8
        buf[off + 1] = v & 0xFF
        off += 2
    if has_v4:
        buf[12] = (v4_int >> 24) & 0xFF
        buf[13] = (v4_int >> 16) & 0xFF
        buf[14] = (v4_int >> 8) & 0xFF
        buf[15] = v4_int & 0xFF
    v6 = bytes(buf)
    # ::ffff:x.x.x.x → V4-mapped (bytes 0-9 zero, 10-11 = 0xFF)
    if (v6[10] == 0xFF and v6[11] == 0xFF
            and v6[0] == 0 and v6[1] == 0 and v6[2] == 0 and v6[3] == 0
            and v6[4] == 0 and v6[5] == 0 and v6[6] == 0 and v6[7] == 0
            and v6[8] == 0 and v6[9] == 0):
        return ((v6[12] << 24) | (v6[13] << 16) | (v6[14] << 8) | v6[15], None)
    return (None, v6)


class QzdbError(Exception):
    """Unified error for QZDB operations.

    Attributes:
        code: One of the class-level error code constants.
    """

    NOT_FOUND = 'NOT_FOUND'
    CORRUPTED = 'CORRUPTED'
    OUT_OF_BOUNDS = 'OUT_OF_BOUNDS'
    INVALID_PARAM = 'INVALID_PARAM'
    BAD_HEADER = 'BAD_HEADER'
    BAD_MAGIC = 'BAD_MAGIC'
    UNSUPPORTED = 'UNSUPPORTED'

    def __init__(self, message: str, code: str | None = None):
        super().__init__(message)
        self.code = code


class GeoInfo:
    __slots__ = ('_values', '_field_names', '_float_indices', '_name_idx', '_norm_idx')

    def __init__(self, values=None, field_names=None, float_indices=None, name_idx=None):
        self._values = values or []
        self._field_names = field_names or []
        self._float_indices = set()
        if name_idx is not None:
            self._name_idx = name_idx
        elif field_names:
            self._name_idx = {n: i for i, n in enumerate(field_names)}
        else:
            self._name_idx = {}
        # Normalized (case/underscore/hyphen-insensitive) index for get() — built
        # once per instance from the canonical field names (API contract §6).
        self._norm_idx = {}
        for i, n in enumerate(self._field_names):
            self._norm_idx.setdefault(_norm_key(n), i)
        if field_names and float_indices:
            self._float_indices = {field_names[i] for i in float_indices if i < len(field_names)}

    def __getattr__(self, name):
        i = self._name_idx.get(name)
        if i is not None:
            return self._values[i] if i < len(self._values) else ''
        raise AttributeError(name)

    def get(self, name):
        """Field access with case/underscore/hyphen-insensitive normalization.

        Returns ``""`` when the field is absent — never raises (API contract §6).
        """
        i = self._norm_idx.get(_norm_key(name))
        if i is not None:
            return self._values[i] if i < len(self._values) else ''
        return ''

    # ── 6 field properties for direct attribute access ────────────
    @property
    def country(self):
        i = self._name_idx.get('country')
        return self._values[i] if i is not None and i < len(self._values) else ''

    @property
    def province(self):
        i = self._name_idx.get('province')
        return self._values[i] if i is not None and i < len(self._values) else ''

    @property
    def city(self):
        i = self._name_idx.get('city')
        return self._values[i] if i is not None and i < len(self._values) else ''

    @property
    def isp(self):
        i = self._name_idx.get('isp')
        return self._values[i] if i is not None and i < len(self._values) else ''

    @property
    def longitude(self):
        i = self._name_idx.get('longitude')
        v = self._values[i] if i is not None and i < len(self._values) else ''
        try: return float(v) if v else None
        except ValueError: return None

    @property
    def latitude(self):
        i = self._name_idx.get('latitude')
        v = self._values[i] if i is not None and i < len(self._values) else ''
        try: return float(v) if v else None
        except ValueError: return None

    # ── semantic getter set (API contract §6) ────────────────────
    # Missing fields return "" (or None for typed getters). get_cidr() is
    # contractually ALWAYS "" because CIDR is not a stored field.

    def get_cidr(self):
        return ''

    # ── typed attribute forms (API contract appendix A.5) ───────────
    # These shadow the raw-string __getattr__ fallback so that direct
    # attribute access returns the same typed values as get_*() methods.

    @property
    def cidr(self):
        # CIDR is not a stored field; contractually always "" (API §6.3).
        return ''

    @property
    def geo_id(self):
        v = self.get('geo_id')
        if not v:
            return None
        try:
            return int(v)
        except ValueError:
            return None

    @property
    def asn(self):
        v = self.get('asn')
        if not v:
            return None
        try:
            return int(v)
        except ValueError:
            return None

    @property
    def usage_type(self):
        return UsageType.from_string(self.get('usage_type'))

    @property
    def country_en(self):
        return self.get('country_en')

    @property
    def province_en(self):
        return self.get('province_en')

    @property
    def city_en(self):
        return self.get('city_en')

    @property
    def district(self):
        return self.get('district')

    def get_geo_id(self):
        v = self.get('geo_id')
        if not v:
            return None
        try:
            return int(v)
        except ValueError:
            return None

    @property
    def timezone(self):
        return self.get('timezone')

    @property
    def isp_en(self):
        return self.get('isp_en')

    def get_asn(self):
        v = self.get('asn')
        if not v:
            return None
        try:
            return int(v)
        except ValueError:
            return None

    @property
    def as_name(self):
        return self.get('as_name')

    @property
    def as_domain(self):
        return self.get('as_domain')

    def get_usage_type(self):
        return UsageType.from_string(self.get('usage_type'))

    @property
    def country_alpha2(self):
        return self.get('country_alpha2')

    @property
    def country_alpha3(self):
        return self.get('country_alpha3')

    @property
    def currency_code(self):
        return self.get('currency_code')

    @property
    def currency_name(self):
        return self.get('currency_name')

    @property
    def phone_prefix(self):
        return self.get('phone_prefix')

    @property
    def emoji_flag(self):
        return self.get('emoji_flag')

    @property
    def languages(self):
        return self.get('languages')

    def to_dict(self):
        return {fname: self._values[i] if i < len(self._values) else ''
                for i, fname in enumerate(self._field_names)}

    def to_map(self):
        """Alias of to_dict() — map of field name → value (all string)."""
        return self.to_dict()

    def to_pipe(self):
        # Values already carry their canonical string form (native floats are
        # decoded to 6-decimal strings in the reader), so just join verbatim.
        parts = []
        for i, fname in enumerate(self._field_names):
            val = self._values[i] if i < len(self._values) else ''
            parts.append(str(val))
        return '|'.join(parts)

    def to_pipe_string(self):
        """Alias of to_pipe() (API contract §6)."""
        return self.to_pipe()

    def to_json(self):
        """Hand-written JSON serialization (API contract §6).

        Preserves the original snake_case keys. ``longitude`` / ``latitude`` /
        ``asn`` / ``geo_id`` are emitted as JSON numbers (``null`` if empty or
        unparsable); all other fields are emitted as JSON strings.
        """
        import json as _json
        out = []
        for i, fname in enumerate(self._field_names):
            val = self._values[i] if i < len(self._values) else ''
            key = _json.dumps(fname, ensure_ascii=False)
            if fname in NUMERIC_FIELDS:
                if val == '':
                    out.append(f'{key}:null')
                else:
                    try:
                        num = float(val)
                        num_out = int(num) if num.is_integer() else num
                        out.append(f'{key}:{_json.dumps(num_out)}')
                    except (ValueError, TypeError):
                        out.append(f'{key}:null')
            else:
                out.append(f'{key}:{_json.dumps(val, ensure_ascii=False)}')
        return '{' + ','.join(out) + '}'

    def __repr__(self):
        return self.to_pipe()

    def __str__(self):
        return self.to_pipe()


# Usage-type metadata indexed by raw value (module-level so it is visible
# during Enum member construction, where class attributes are not yet reachable).
_USAGE_META = {
    "AICrawler": ("AI 爬虫", "AICrawler", "AI 训练 / AI 搜索爬虫（GPTBot、ClaudeBot 等）"),
    "Backbone": ("骨干网", "Backbone", "运营商骨干传输网 / 国际出口"),
    "Broadband": ("宽带", "Broadband", "家庭/企业宽带接入（xDSL、光纤、Cable、拨号等）"),
    "Business": ("企业", "Business", "企业专线 / 企业组网"),
    "CDN": ("CDN", "CDN", "内容分发网络"),
    "Cloud": ("云服务", "Cloud", "公有云 / 托管云（AWS、阿里云、Azure 等）"),
    "DNS": ("DNS", "DNS", "DNS 基础设施 / Anycast DNS"),
    "DataCenter": ("数据中心", "DataCenter", "IDC / 机房托管"),
    "Education": ("教育网", "Education", "高校 / 科研网（CERNET 等）"),
    "Finance": ("金融", "Finance", "银行 / 证券 / 保险等金融机构"),
    "Government": ("政府", "Government", "政务 / 公共机构网络"),
    "ISP": ("互联网提供商", "ISP", "未细分类型的通用 ISP 接入"),
    "IXP": ("交换中心", "IXP", "互联网交换中心"),
    "IoT": ("物联网", "IoT", "物联网设备接入网络"),
    "Mobile": ("移动网络", "Mobile", "蜂窝移动网络（2G/3G/4G/5G）"),
    "Reserved": ("保留地址", "Reserved", "保留 / 未分配地址"),
    "Satellite": ("卫星互联网", "Satellite", "卫星 / 低轨星座接入（Starlink 等）"),
    "Spider": ("爬虫", "Spider", "通用搜索引擎 / 通用网络爬虫"),
    "Streaming": ("流媒体", "Streaming", "音视频 / 直播流媒体平台"),
    "Unknown": ("未知", "Unknown", "无法判定用途"),
    "VPN": ("VPN/代理", "VPN", "VPN / 代理 / 隐私网络出口"),
}


class UsageType(str, Enum):
    """IP network usage-type (API contract §6 / §7, appendix A.5).

    ``str, Enum`` so known members compare equal to their raw string value
    (``UsageType.CDN == 'CDN'``) and serialize as plain strings. ``from_string`` /
    ``from_raw`` return the enum member for a recognized raw value and the raw
    ``str`` itself for anything else (``UsageType | str`` per appendix A.5).
    """

    AICRAWLER = "AICrawler"
    BACKBONE = "Backbone"
    BROADBAND = "Broadband"
    BUSINESS = "Business"
    CDN = "CDN"
    CLOUD = "Cloud"
    DNS = "DNS"
    DATACENTER = "DataCenter"
    EDUCATION = "Education"
    FINANCE = "Finance"
    GOVERNMENT = "Government"
    ISP = "ISP"
    IXP = "IXP"
    IOT = "IoT"
    MOBILE = "Mobile"
    RESERVED = "Reserved"
    SATELLITE = "Satellite"
    SPIDER = "Spider"
    STREAMING = "Streaming"
    UNKNOWN = "Unknown"
    VPN = "VPN"

    def __init__(self, value):
        zh, en, desc = _USAGE_META.get(value, ("未知", value, f"未知用途: {value}"))
        self._zh = zh
        self._en = en
        self._desc = desc
        self._raw = value
        self._known = True

    # accessors (explicit get_* form, API contract §6.x)
    def raw_value(self):
        return self._raw

    def get_display_zh(self):
        return self._zh

    def get_display_en(self):
        return self._en

    def get_description(self):
        return self._desc

    def is_known(self):
        return self._known

    # spec-named attribute forms (appendix A.5)
    @property
    def raw(self):
        return self._raw

    @property
    def display_zh(self):
        return self._zh

    @property
    def display_en(self):
        return self._en

    @property
    def description(self):
        return self._desc

    @classmethod
    def from_string(cls, raw):
        if raw is None or raw == '':
            return cls.UNKNOWN
        known = cls._KNOWN.get(raw.lower())
        if known is not None:
            return known
        return raw  # spec: unknown -> raw str

    @classmethod
    def from_raw(cls, raw):
        return cls.from_string(raw)

    def __repr__(self):
        return f'UsageType({self._raw!r}, known={self._known})'


# Raw -> member lookup (preserves old _KNOWN semantics for callers/tests).
UsageType._KNOWN = {m.value.lower(): m for m in UsageType}


class QzdbReader:
    # No singleton. v2.4 removed get_instance() in every language: a process-wide
    # mutable instance made "which file am I querying?" unanswerable under
    # concurrency. Construct readers directly (they are cheap and thread-safe for
    # reads) and use QzdbRegistry when you want process-wide sharing by path.

    def close(self):
        """Release mapped file resources. Idempotent — safe to call multiple times."""
        if hasattr(self, '_is_mmap') and self._is_mmap and hasattr(self._data, 'close'):
            try:
                self._data.close()
            except OSError:
                pass
        self._data = b''
        self._is_mmap = False
        self._closed = True

    def __enter__(self):
        """Context-manager entry (API contract appendix A.5)."""
        return self

    def __exit__(self, exc_type, exc_val, exc_tb):
        """Context-manager exit — always releases resources."""
        self.close()
        return False

    def __del__(self):
        self.close()

    def __init__(self, db_path=None, group_index=0, verify_crc=True):
        self._data = b''
        self._is_mmap = False
        self._group_index = group_index
        # §10.6: CRC32 verification is ON by default at open time. Pass
        # verify_crc=False only for diagnostics/benchmarks on trusted data.
        self._verify_crc = verify_crc
        self._field_names = []
        self._float_field_indices = set()
        self._version_name = ''
        self._name_idx = {}
        self._norm_idx = {}

        # Header fields
        self._flags = 0
        self._version_mask = 0
        self._has_v4 = False
        self._has_v6 = False
        self._v4_node_24 = False
        self._v6_node_24 = False
        self._v6_jump_bits = 16
        self._pool_count = 0
        self._pool_idx_size = 2
        self._geo_count = 0
        self._row_count = 0
        self._v4_rec_count = 0
        self._v6_rec_count = 0
        self._v4_node_count = 0
        self._v6_node_count = 0
        self._ip_row_size = 6
        self._geo_entry_group_count = 0

        # Metadata (resolved in _resolve_field_names)
        self._build_date = 0
        self._description = ''
        self._primary_version = ''
        self._edition = ''
        self._edition_source = EDITION_SOURCE_UNKNOWN
        self._field_names_source = FIELD_NAMES_SOURCE_SYNTHETIC
        self._group_ids = []
        self._group_field_names = []
        self._group_name_idx = []
        self._group_float_indices = []

        # Lifecycle / performance cache
        self._closed = False
        # per-snapshot bounded lock-free GeoInfo cache, keyed by (group_index, entry_id).
        # On collision (capacity reached) we simply skip caching and recompute —
        # never returning a value for the wrong key (API contract §3 / §9).
        self._geo_cache = {}
        self._geo_cache_max = 1 << 16

        # Offsets
        self._off_v4_jump = 0
        self._off_v4_nodes = 0
        self._off_v6_jump = 0
        self._off_v6_nodes = 0
        self._off_ip_row = 0
        self._off_geo_entries = 0
        self._off_pools = 0
        self._off_meta = 0
        self._off_row_schema = 0
        self._off_group_schema = 0

        # Schema and layout cache
        self._group_field_counts = []
        self._group_entry_counts = []
        self._group_dim_masks = []
        self._group_entry_offsets = []

        self._group_strides = []
        self._group_field_widths = []
        self._group_field_offsets = []
        self._group_field_native = []
        self._group_field_native_type = []

        self._group_pools = None
        self._pools_loaded = False
        self._pools_lock = threading.Lock()

        if db_path is not None:
            self.load(db_path)

    def load(self, db_path, verify_crc=None):
        if verify_crc is not None:
            self._verify_crc = verify_crc
        try:
            f = open(db_path, 'rb')
        except FileNotFoundError:
            raise QzdbError(f'Database file not found: {db_path}', QzdbError.NOT_FOUND)
        except OSError as exc:
            raise QzdbError(f'Failed to read database file: {exc}', QzdbError.CORRUPTED) from exc

        try:
            fsize = os.fstat(f.fileno()).st_size
            if fsize >= 1024 * 1024:  # 1MB threshold → mmap for lazy loading
                data = mmap.mmap(f.fileno(), fsize, access=mmap.ACCESS_READ)
                is_mmap = True
            else:
                data = f.read()
                is_mmap = False
        except OSError as exc:
            f.close()
            raise QzdbError(f'Failed to memory-map database: {exc}', QzdbError.CORRUPTED) from exc
        finally:
            f.close()

        shadow = self._build_shadow(data, is_mmap)
        self._publish(shadow)

    def _build_shadow(self, data, is_mmap):
        """Build a fully-parsed shadow snapshot from raw bytes (file or buffer).

        A partial load never mutates the live instance, so a parse failure leaves
        the running reader fully intact (Fail-Closed, API contract §2/§4).
        """
        shadow = QzdbReader.__new__(QzdbReader)
        # Seed shadow with this instance's runtime state so methods touching
        # pools/offsets don't hit AttributeError before _parse_header repopulates
        # them (shadow is __new__-born and never runs __init__).
        shadow.__dict__.update(self.__dict__)
        shadow._data = data
        shadow._is_mmap = is_mmap
        shadow._verify_crc = self._verify_crc
        shadow._group_index = self._group_index
        # Reset lazy-pool flags so the new file rebuilds its own pools.
        shadow._pools_loaded = False
        shadow._group_pools = None
        shadow._pools_lock = threading.Lock()
        # Fresh per-snapshot GeoInfo cache (old snapshot's cache is dropped).
        shadow._geo_cache = {}
        shadow._closed = False
        try:
            shadow._parse_header()
            if shadow._verify_crc and not shadow.verify_crc():
                raise QzdbError(
                    'CRC32 checksum mismatch — the .qzdb file is corrupted or truncated',
                    QzdbError.CORRUPTED,
                )
        except Exception:
            # Shadow failed — close its mmap if it opened one, then re-raise.
            if shadow._is_mmap and hasattr(shadow._data, 'close'):
                try:
                    shadow._data.close()
                except OSError:
                    pass
            raise
        return shadow

    def _publish(self, shadow):
        """Atomically swap in a fully-built shadow snapshot (API contract §2/§3)."""
        old_data = self._data
        # Do NOT alias the dict: sharing shadow.__dict__ lets shadow's
        # __del__ -> close() wipe self._data to b'', breaking every find() call.
        self.__dict__.clear()
        self.__dict__.update(shadow.__dict__)
        # Disarm shadow so its destructor cannot close the mmap we took over.
        shadow._is_mmap = False
        shadow._data = b''
        self._closed = False
        if hasattr(old_data, 'close'):
            try:
                old_data.close()
            except OSError:
                pass

    def reload(self, path):
        """Hot-swap to a new database file. CRC is ALWAYS forced (API contract §2).

        On failure the old snapshot keeps serving and a QzdbError is raised.
        """
        if not os.path.exists(path) or not os.access(path, os.R_OK):
            raise QzdbError(f'Reload file does not exist: {path}', QzdbError.NOT_FOUND)
        saved_verify = self._verify_crc
        self._verify_crc = True  # reload forces CRC regardless of open option
        try:
            f = open(path, 'rb')
        except OSError as exc:
            self._verify_crc = saved_verify
            raise QzdbError(f'Failed to read reload file: {path}', QzdbError.CORRUPTED) from exc
        try:
            fsize = os.fstat(f.fileno()).st_size
            if fsize >= 1024 * 1024:
                data = mmap.mmap(f.fileno(), fsize, access=mmap.ACCESS_READ)
                is_mmap = True
            else:
                data = f.read()
                is_mmap = False
        except OSError as exc:
            f.close()
            self._verify_crc = saved_verify
            raise QzdbError(f'Failed to memory-map reload file: {exc}', QzdbError.CORRUPTED) from exc
        finally:
            f.close()
        shadow = self._build_shadow(data, is_mmap)
        self._verify_crc = saved_verify
        self._publish(shadow)

    def reload_buffer(self, buffer):
        """Hot-swap to a new in-memory buffer (copy semantics). CRC forced."""
        if buffer is None or len(buffer) == 0:
            raise QzdbError('Reload buffer cannot be null or empty', QzdbError.INVALID_PARAM)
        saved_verify = self._verify_crc
        self._verify_crc = True
        data = bytes(buffer)  # copy protection
        try:
            shadow = self._build_shadow(data, False)
        finally:
            self._verify_crc = saved_verify
        self._publish(shadow)

    @staticmethod
    def open_buffer(buffer, group_index=0, verify_crc=True):
        """Build a reader directly from an in-memory buffer (copy semantics).

        The buffer bytes are copied (never aliased) so the caller may free or
        mutate the original afterwards (API contract §4.1 / appendix A.5).
        CRC32 is verified by the underlying reload path before serving.
        """
        if buffer is None or len(buffer) == 0:
            raise QzdbError('open_buffer buffer cannot be null or empty', QzdbError.INVALID_PARAM)
        r = QzdbReader(group_index=group_index, verify_crc=verify_crc)
        r.reload_buffer(buffer)
        return r

    # ---- Fail-Closed primitive readers -------------------------------------
    # §7 / appendix A.8: a malformed or hostile .qzdb must NEVER surface a raw
    # ``struct.error`` / ``IndexError`` / ``OverflowError`` to the caller — every
    # out-of-range access is translated into a structured ``QzdbError``.
    # The ``try`` blocks are zero-cost on the happy path in CPython (the
    # exception table is only consulted when an exception actually fires), so
    # the hot query path keeps its original speed.
    def _oob(self, off, need):
        raise QzdbError(
            f'Read out of bounds: offset={off}, need={need}, size={len(self._data)}',
            QzdbError.CORRUPTED,
        )

    def safe_read_u16(self, off):
        try:
            return struct.unpack_from('<H', self._data, off)[0]
        except (struct.error, IndexError, OverflowError, TypeError, ValueError):
            self._oob(off, 2)

    def safe_read_u32(self, off):
        try:
            return struct.unpack_from('<I', self._data, off)[0]
        except (struct.error, IndexError, OverflowError, TypeError, ValueError):
            self._oob(off, 4)

    def safe_read_u64(self, off):
        try:
            return struct.unpack_from('<Q', self._data, off)[0]
        except (struct.error, IndexError, OverflowError, TypeError, ValueError):
            self._oob(off, 8)

    def safe_read_u8(self, off):
        try:
            return self._data[off]
        except (IndexError, OverflowError, TypeError, ValueError):
            self._oob(off, 1)

    def safe_read_u24(self, off):
        d = self._data
        try:
            return d[off] | (d[off + 1] << 8) | (d[off + 2] << 16)
        except (IndexError, OverflowError, TypeError, ValueError):
            self._oob(off, 3)

    def safe_read_u48(self, off):
        d = self._data
        try:
            return (d[off]
                    | (d[off + 1] << 8)
                    | (d[off + 2] << 16)
                    | (d[off + 3] << 24)
                    | (d[off + 4] << 32)
                    | (d[off + 5] << 40))
        except (IndexError, OverflowError, TypeError, ValueError):
            self._oob(off, 6)

    def safe_read_uint_width(self, off, width):
        if width <= 1:
            return self.safe_read_u8(off)
        elif width == 2:
            return self.safe_read_u16(off)
        elif width == 3:
            return self.safe_read_u24(off)
        else:
            return self.safe_read_u32(off)

    # Native scalar field decoder — matches Java readNativeValue() exactly so that
    # cross-language golden vectors are byte-identical:
    #   - float32/float64 -> 6 decimal places (FLOAT6 / "0.000000"); integer-valued
    #     floats drop the decimals (e.g. 116.0 -> "116"); NaN/Inf -> "".
    #   - integer -> decimal string (unsigned for width <= 4).
    def _decode_native(self, nat_type, w, fo):
        d = self._data
        if nat_type == 1:
            try:
                fv = struct.unpack_from('<f', d, fo)[0] if w == 4 else struct.unpack_from('<d', d, fo)[0]
            except (struct.error, IndexError, OverflowError, TypeError, ValueError):
                self._oob(fo, 4 if w == 4 else 8)
            if fv != fv or fv in (float('inf'), float('-inf')):
                return ''
            if fv == int(fv):
                return str(int(fv))
            return f'{fv:.6f}'
        return str(self.safe_read_uint_width(fo, w))

    def _parse_header(self):
        d = self._data
        if len(d) < 192:
            raise QzdbError('File too small for QZDB header', QzdbError.CORRUPTED)

        magic = d[:4]
        if magic != b'QZDB':
            raise QzdbError('Invalid magic, expected QZDB', QzdbError.BAD_MAGIC)

        fmt_ver = d[4]
        # §10.1: QZDB's unified HeaderVersion is always 1 (carries GROUP_SCHEMA
        # dynamic-width schema + native scalar support). Older experimental
        # layouts never shipped and are intentionally unsupported — rejecting
        # them here avoids silently mis-parsing a legacy file.
        if fmt_ver != 1:
            raise QzdbError(
                f'Unsupported HeaderVersion: {fmt_ver} (QZDB requires version 1, see FORMAT.md §10.1)',
                QzdbError.UNSUPPORTED,
            )

        self._version_mask = self.safe_read_u16(6)
        self._flags = self.safe_read_u16(8)
        self._has_v4 = bool(self._flags & 1)
        self._has_v6 = bool(self._flags & 2)
        self._v4_node_24 = bool(self._flags & 0x10)
        self._v6_node_24 = bool(self._flags & 0x20)

        self._build_date = self.safe_read_u32(32)

        self._v6_jump_bits = d[11]
        if self._v6_jump_bits == 0:
            self._v6_jump_bits = 16
        # §4.2: V6JumpBits is dynamically estimated in the range 8~20.
        if self._v6_jump_bits < 8 or self._v6_jump_bits > 20:
            raise QzdbError(f'v6_jump_bits out of range [8,20]: {self._v6_jump_bits}', QzdbError.INVALID_PARAM)

        self._pool_count = d[12]
        self._pool_idx_size = d[13]
        if self._pool_idx_size not in (2, 3):
            raise QzdbError(f'pool_idx_size must be 2 or 3, got {self._pool_idx_size}', QzdbError.INVALID_PARAM)
        self._geo_count = self.safe_read_u16(14)
        self._row_count = self.safe_read_u32(20)
        self._v4_rec_count = self.safe_read_u32(24)
        self._v6_rec_count = self.safe_read_u32(28)

        hs = self.safe_read_u32(36)
        if hs != 192:
            raise QzdbError(f'Unexpected header size: {hs}', QzdbError.BAD_HEADER)

        # Offsets
        self._off_row_schema = self.safe_read_u64(40)
        self._off_group_schema = self.safe_read_u64(48)
        self._off_v4_jump = self.safe_read_u64(64)
        self._off_v4_nodes = self.safe_read_u64(72)
        self._off_v6_jump = self.safe_read_u64(80)
        self._off_v6_nodes = self.safe_read_u64(88)
        self._off_ip_row = self.safe_read_u64(96)
        self._off_geo_entries = self.safe_read_u64(104)
        self._off_pools = self.safe_read_u64(136)
        self._off_meta = self.safe_read_u64(144)

        self._v4_node_count = self.safe_read_u32(152)
        self._v6_node_count = self.safe_read_u32(156)
        self._ip_row_size = self.safe_read_u32(160)
        if self._ip_row_size < 1 or self._ip_row_size > 64:
            raise QzdbError(f'ip_row_size out of range [1,64]: {self._ip_row_size}', QzdbError.INVALID_PARAM)
        self._geo_entry_group_count = self.safe_read_u32(164)
        if self._geo_entry_group_count < 1 or self._geo_entry_group_count > 255:
            raise QzdbError(f'geo_entry_group_count out of range [1,255]: {self._geo_entry_group_count}', QzdbError.INVALID_PARAM)

        # Bounds validation: raise on corrupt files instead of OOB reads.
        #
        # Every section offset in the 192-byte header is attacker-controlled, so
        # each one is validated against the real buffer length *here*, at parse
        # time — never deferred to the query hot path. ``required`` is compared
        # as ``required > dlen - offset`` so the addition can never overflow
        # (mirrors the Go ``checkOffset`` / Rust ``check_offset`` helpers).
        dlen = len(d)
        node_size = 6 if self._v4_node_24 else 8
        v6_node_size = 6 if self._v6_node_24 else 8

        def _chk(offset, required, field):
            if offset == 0:
                return
            if offset > dlen or required > dlen - offset:
                raise QzdbError(
                    f'Section {field} out of bounds (offset={offset}, need={required}, size={dlen})',
                    QzdbError.CORRUPTED,
                )

        # Mandatory trie/row sections.
        _chk(self._off_v4_jump, 65536 * 4, 'v4_jump')
        _chk(self._off_v4_nodes, self._v4_node_count * node_size, 'v4_nodes')
        _chk(self._off_v6_jump, (1 << self._v6_jump_bits) * 4, 'v6_jump')
        _chk(self._off_v6_nodes, self._v6_node_count * v6_node_size, 'v6_nodes')
        _chk(self._off_ip_row, self._row_count * self._ip_row_size, 'ip_row')
        # Optional/variable-length sections: only the minimum header of each
        # section is validated here; the walkers below re-check every step.
        _chk(self._off_geo_entries, 1, 'geo_entries')
        _chk(self._off_pools, 4, 'pools')
        _chk(self._off_meta, 4, 'meta')
        _chk(self._off_row_schema, 4, 'row_schema')
        _chk(self._off_group_schema, 2, 'group_schema')

        # ROW_SCHEMA parsing (v5 dynamic-width IPRow schema).
        # On-disk layout (matches C# QZDBReader.ParseRowSchema AND the builder's
        # WriteQzdbFile serialization):
        #   byte[sp+0]     = fieldCount
        #   byte[sp+1]     = stride (== IPRowSize)
        #   byte[sp+2..3]  = reserved (uint16)
        #   then per field (4 bytes): FieldId(1) | Width(1) | FieldOffset(1) | flags(1)
        # Field ids: 0 = geo_id, 1 = asn_id, 2 = usage_type_id.
        #
        # NOTE: an earlier parser read fieldCount at sp+5 and the first field at
        # sp+9, which skipped the geo dimension (fid=0) and mis-read the asn row
        # width. That collapsed every real ASN to the default 56554 entry — the
        # "解析不对" regression that caused customer refunds.
        self._row_geo_width = 3
        self._row_asn_width = 3
        self._row_usage_width = 0
        if self._off_row_schema > 0:
            sp = self._off_row_schema
            f_count = d[sp] & 0xFF
            schema_stride = d[sp + 1] & 0xFF
            wpos = sp + 4
            # The declared field count is attacker-controlled; refuse a schema
            # whose descriptor array does not fit in the buffer instead of
            # walking past the end of the file.
            if f_count * 4 > dlen - wpos:
                raise QzdbError(
                    f'ROW_SCHEMA truncated (fieldCount={f_count}, offset={sp}, size={dlen})',
                    QzdbError.CORRUPTED,
                )
            geo_w, asn_w, usage_w = 0, 0, 0
            for _ in range(f_count):
                fid = d[wpos]
                w = d[wpos + 1]
                if fid == 0:
                    geo_w = w
                elif fid == 1:
                    asn_w = w
                elif fid == 2:
                    usage_w = w
                wpos += 4
            if schema_stride == self._ip_row_size and (geo_w + asn_w + usage_w) == self._ip_row_size:
                self._row_geo_width = geo_w
                self._row_asn_width = asn_w
                self._row_usage_width = usage_w
            # else (schema absent or inconsistent): keep fallback defaults (geo=3, asn=3, usage=0)

        # GeoEntryOffsets[4]
        self._group_entry_offsets = []
        for i in range(4):
            self._group_entry_offsets.append(self.safe_read_u48(168 + i * 6))

        # Parse GroupMetadataTable (at off_geo_entries).
        # §6.2: current format stores, per group, a 1-byte fieldCount, a
        # uint32 LE entryCount, and a uint16 LE dimensionMask — fixed widths,
        # no version-dependent layout. (HeaderVersion != 1 is already rejected
        # above, so no legacy width branching is needed here.)
        gm_off = self._off_geo_entries
        group_count = self.safe_read_u8(gm_off)
        gm_off += 1

        actual_groups = min(group_count, max(1, self._geo_entry_group_count))
        if actual_groups > 4:
            actual_groups = 4
        # A file with zero groups carries no queryable dimension at all; reject
        # it rather than building an empty reader that would IndexError later
        # (same contract as the Rust/Go readers).
        if actual_groups < 1:
            raise QzdbError('GroupMetadataTable groupCount is 0', QzdbError.CORRUPTED)
        # Each group entry is fieldCount(1) + entryCount(4) + dimensionMask(2).
        if actual_groups * 7 > dlen - gm_off:
            raise QzdbError('GroupMetadataTable truncated', QzdbError.CORRUPTED)
        self._group_field_counts = [0] * actual_groups
        self._group_entry_counts = [0] * actual_groups
        self._group_dim_masks = [0] * actual_groups
        # Per-group edition bitmask from GROUP_SCHEMA.groupId (0 = not declared).
        self._group_ids = [0] * actual_groups

        for gi in range(actual_groups):
            self._group_field_counts[gi] = self.safe_read_u8(gm_off)
            gm_off += 1
            # §6.2: entryCount is always uint32 LE in the current format.
            self._group_entry_counts[gi] = self.safe_read_u32(gm_off)
            gm_off += 4
            # §6.2: dimensionMask is always present (uint16 LE). A value of 0
            # only occurs in a malformed/legacy file and is repaired below from
            # the group's actual field composition (no groupIndex hardcoding).
            self._group_dim_masks[gi] = self.safe_read_u16(gm_off)
            gm_off += 2

        # Initialize schema and widths
        self._group_strides = [0] * actual_groups
        self._group_field_widths = [None] * actual_groups
        self._group_field_offsets = [None] * actual_groups
        self._group_field_native = [None] * actual_groups
        self._group_field_native_type = [None] * actual_groups

        # Field count declared by GROUP_SCHEMA for each group. This is a *second*
        # source of truth next to GroupMetadataTable's fieldCount; a forged file
        # can make the two disagree, and every consumer indexes the width /
        # offset / native arrays by the GroupMetadataTable value. The mismatch is
        # reconciled right after the walk (see the invariant block below).
        schema_fld_counts = [None] * actual_groups

        # Parse GROUP_SCHEMA if present
        if self._off_group_schema > 0:
            sp = self._off_group_schema
            gs_group_count = self.safe_read_u16(sp)
            sp += 2
            max_gs_groups = min(gs_group_count, actual_groups)
            for gi in range(max_gs_groups):
                # Per-group header is 16 bytes: groupId(2) fldCount(2)
                # entryCount(4) stride(4) flags(4). Stop the walk as soon as the
                # declared layout would run past the buffer — the groups already
                # parsed stay valid and the rest fall back to defaults below.
                if 16 > dlen - sp:
                    break
                # groupId is the per-group one-hot edition bitmask (FORMAT §3.1).
                # It is the authoritative edition signal for this group and must
                # not be skipped — see _resolve_field_names().
                group_id = self.safe_read_u16(sp)
                sp += 2
                if gi < len(self._group_ids):
                    self._group_ids[gi] = group_id
                fld_count = self.safe_read_u16(sp)
                sp += 2
                sp += 4  # skip entryCount (uint32)
                stride = self.safe_read_u32(sp)
                sp += 4
                sp += 4  # skip flags
                if fld_count * 12 > dlen - sp:
                    break
                schema_fld_counts[gi] = fld_count

                if gi < actual_groups:
                    self._group_strides[gi] = stride
                    widths = [0] * fld_count
                    offsets = [0] * fld_count
                    natives = [False] * fld_count
                    nat_types = [0] * fld_count
                    for fi in range(fld_count):
                        # fieldId is just the slot ordinal (0..N-1); it carries no
                        # cross-edition semantics, so it is read and discarded.
                        sp += 2
                        widths[fi] = d[sp]
                        sp += 1
                        field_flags = d[sp]
                        sp += 1
                        natives[fi] = (field_flags & 0x01) != 0
                        nat_types[fi] = (field_flags >> 1) & 0x03
                        offsets[fi] = self.safe_read_u32(sp)
                        sp += 4
                        sp += 4  # skip poolSectionId
                    self._group_field_widths[gi] = widths
                    self._group_field_offsets[gi] = offsets
                    self._group_field_native[gi] = natives
                    self._group_field_native_type[gi] = nat_types
                else:
                    sp += fld_count * 12

        # INVARIANT: len(widths/offsets/native/native_type[g]) == group_field_counts[g].
        #
        # GroupMetadataTable and GROUP_SCHEMA each declare a field count. Real
        # files always agree, but a forged file can shrink the GROUP_SCHEMA one
        # while leaving GroupMetadataTable large — every downstream consumer
        # (pool loader, field extractor) iterates `range(group_field_counts[g])`
        # and would then index past the end of the schema arrays. Drop the
        # inconsistent schema entirely and let the default layout below rebuild
        # it (same reconciliation as the Rust/Go/C readers).
        for g in range(actual_groups):
            if schema_fld_counts[g] is None:
                continue
            if schema_fld_counts[g] != self._group_field_counts[g]:
                self._group_strides[g] = 0
                self._group_field_widths[g] = None
                self._group_field_offsets[g] = None
                self._group_field_native[g] = None
                self._group_field_native_type[g] = None

        # Fallback for groups without schema info
        for g in range(actual_groups):
            if self._group_strides[g] == 0:
                self._group_strides[g] = self._group_field_counts[g] * self._pool_idx_size
            if self._group_field_widths[g] is None:
                self._group_field_widths[g] = [self._pool_idx_size] * self._group_field_counts[g]
            if self._group_field_offsets[g] is None:
                self._group_field_offsets[g] = [i * self._pool_idx_size for i in range(self._group_field_counts[g])]
            if self._group_field_native[g] is None:
                self._group_field_native[g] = [False] * self._group_field_counts[g]
            if self._group_field_native_type[g] is None:
                self._group_field_native_type[g] = [0] * self._group_field_counts[g]

        self._resolve_field_names()
        self._repair_dim_masks()

    def _repair_dim_masks(self):
        """Metadata-driven fallback for dimensionMask (§5.4 / §6.2).

        A valid current-format file always stores a non-zero dimensionMask in
        GroupMetadataTable, so this normally does nothing. If a group's mask is
        0 (malformed/legacy), we derive it from the group's *resolved field
        names* — never from ``fieldId`` (which is only the slot ordinal
        0..N-1 and carries no cross-edition meaning) and never from the group
        index (the real asn file keeps its asn group at index 0 with a stored
        mask of 0x02, so an index-based rule would be wrong).
        """
        asn_key = _norm_key('asn')
        for g in range(len(self._group_dim_masks)):
            if self._group_dim_masks[g] != 0:
                continue
            names = self._group_field_names[g] if g < len(self._group_field_names) else None
            has_asn = any(_norm_key(n) == asn_key for n in (names or ()))
            self._group_dim_masks[g] = 0x02 if has_asn else 0x01

    def _resolve_field_names(self):
        """Resolve edition + field names from the file's own self-description.

        Both answers are derived from what the file declares, in a strict
        precedence order that is identical in all 8 SDKs (FORMAT §10.3):

        edition      1. GROUP_SCHEMA.groupId / Header.VersionMask one-hot bit
                     2. Metadata primary_version, or a single-entry version_list
                     3. unambiguous field-count match (last resort)
                     4. '' (unknown) — we do not invent an answer
        field_names  1. Metadata field_names, when its arity matches the group
                     2. canonical table for a *known* edition of matching arity
                     3. field_0..field_N-1 placeholders

        get_edition_source() / get_field_names_source() report which rule fired,
        so callers can tell a name read off disk from one filled in by the SDK.
        """
        d = self._data
        off_meta = self._off_meta
        gi = self._group_index if self._group_index < len(self._group_field_counts) else 0

        # --- Metadata TLV walk -------------------------------------------------
        meta_field_names = None
        if (self._flags & 4) and off_meta > 0 and off_meta + 4 <= len(d):
            pos = off_meta
            while pos + 4 <= len(d):
                t = d[pos]
                length = self.safe_read_u16(pos + 2)
                if t == 0 or length == 0:
                    break
                # A forged TLV can claim a length that runs past EOF; stop the
                # walk instead of decoding a truncated (and possibly invalid)
                # UTF-8 tail. Well-formed but non-UTF-8 payloads are decoded
                # lossily, matching the Go/Rust readers' behaviour.
                if length > len(d) - (pos + 4):
                    break
                val = d[pos + 4:pos + 4 + length].decode('utf-8', errors='replace')
                if t == 1:
                    self._version_name = val
                elif t == 2:
                    meta_field_names = val.split('|')
                elif t == 3:
                    self._description = val
                elif t == 4:
                    self._primary_version = val
                # Unknown types are skipped by design (FORMAT §8.1).
                pos += 4 + length

        # --- Resolve every group, not just the active one -----------------------
        # dimensionMask repair and per-group field-name lookups both need the
        # names of groups other than the current one, so the whole table is
        # resolved up front (same shape as the Node/Go/Rust/C readers).
        group_count = len(self._group_field_counts)
        self._group_field_names = [None] * group_count
        self._group_name_idx = [None] * group_count
        self._group_float_indices = [None] * group_count
        group_editions = [''] * group_count
        group_edition_sources = [EDITION_SOURCE_UNKNOWN] * group_count
        group_name_sources = [FIELD_NAMES_SOURCE_SYNTHETIC] * group_count

        for g in range(group_count):
            n_fields = self._group_field_counts[g]

            # edition: this group's own bitmask first, then the file-level mask.
            mask = self._group_ids[g] if g < len(self._group_ids) and self._group_ids[g] else self._version_mask
            edition = edition_from_mask(mask)
            source = EDITION_SOURCE_VERSION_MASK
            if not edition:
                edition = self._primary_version.strip()
                if not edition:
                    tokens = [t.strip() for t in self._version_name.split(',') if t.strip()]
                    if len(tokens) == 1:
                        edition = tokens[0]
                if edition:
                    source = EDITION_SOURCE_METADATA
            if not edition:
                edition = _EDITION_BY_FIELD_COUNT.get(n_fields) or ''
                source = EDITION_SOURCE_INFERRED if edition else EDITION_SOURCE_UNKNOWN

            # field names
            if meta_field_names and len(meta_field_names) == n_fields:
                g_names = list(meta_field_names)
                g_names_source = FIELD_NAMES_SOURCE_METADATA
            else:
                canonical = EDITION_FIELD_NAMES.get(edition)
                if canonical and len(canonical) == n_fields:
                    g_names = list(canonical)
                    g_names_source = FIELD_NAMES_SOURCE_EDITION
                else:
                    g_names = [f'field_{i}' for i in range(n_fields)]
                    g_names_source = FIELD_NAMES_SOURCE_SYNTHETIC

            self._group_field_names[g] = g_names
            group_editions[g] = edition
            group_edition_sources[g] = source
            group_name_sources[g] = g_names_source
            # Per-group lookup tables, built once here so _resolve_geo() never
            # pays for rebuilding them and never borrows another group's names.
            g_name_idx = {}
            for i, n in enumerate(g_names):
                g_name_idx.setdefault(n, i)
            self._group_name_idx[g] = g_name_idx
            self._group_float_indices[g] = {i for i, n in enumerate(g_names) if n in FLOAT_FIELDS}

        names = self._group_field_names[gi] if gi < group_count else []
        self._edition = group_editions[gi] if gi < group_count else ''
        self._edition_source = group_edition_sources[gi] if gi < group_count else EDITION_SOURCE_UNKNOWN
        self._field_names = names
        self._field_names_source = (group_name_sources[gi] if gi < group_count
                                    else FIELD_NAMES_SOURCE_SYNTHETIC)
        self._name_idx = {n: i for i, n in enumerate(names)}
        self._norm_idx = {}
        for i, n in enumerate(names):
            self._norm_idx.setdefault(_norm_key(n), i)
        self._float_field_indices = {i for i, n in enumerate(names) if n in FLOAT_FIELDS}

    def _ensure_pools_loaded(self):
        if self._pools_loaded:
            return
        with self._pools_lock:
            if self._pools_loaded:
                return

            group_count = len(self._group_field_counts)
            self._group_pools = [None] * group_count

            if self._off_pools <= 0:
                return

            pool_cursor = self._off_pools
            pool_end = self._off_meta if self._off_meta > 0 else len(self._data)
            d = self._data

            for g in range(group_count):
                field_count = self._group_field_counts[g]
                group_pool_list = []
                natives = self._group_field_native[g]
                for f in range(field_count):
                    if natives and f < len(natives) and natives[f]:
                        group_pool_list.append([])
                        continue

                    if pool_cursor + 4 > pool_end:
                        group_pool_list.append([])
                        continue
                    count = self.safe_read_u32(pool_cursor)
                    pool_cursor += 4
                    if self._off_row_schema > 0:
                        pool_cursor += 4
                    # Security guard: unbounded count would OOM on count+1 offsets.
                    if count == 0 or count > MAX_POOL_COUNT:
                        group_pool_list.append([])
                        continue

                    # The (count + 1) offset table must fit inside the POOLS
                    # section; `count` came straight off disk, so this is the
                    # boundary that stops a forged pool header from walking the
                    # cursor past EOF.
                    if pool_cursor > pool_end or (count + 1) * 4 > pool_end - pool_cursor:
                        group_pool_list.append([])
                        continue

                    # Read string offsets (batch unpack for performance)
                    fmt = f'<{count + 1}I'
                    offsets = list(struct.unpack_from(fmt, d, pool_cursor))
                    pool_cursor += (count + 1) * 4

                    # Read string data
                    str_base = pool_cursor
                    avail = pool_end - str_base
                    # The offset table is cumulative: offsets[i+1] >= offsets[i]
                    # and the last entry is the total byte length. Monotonicity
                    # MUST be enforced, not merely assumed -- a forged table can
                    # otherwise make every entry span the whole section, so
                    # `count` slices of section-length each amplify into
                    # gigabytes of decoded text (measured 138977 x 11 MB = 7.2 GB
                    # on a single flipped header byte). With start >= prev_end and
                    # end <= tail the slices are disjoint inside [0, tail], which
                    # caps total decoded bytes at `tail`.
                    tail = offsets[count]
                    if tail > avail:
                        group_pool_list.append([])
                        continue
                    strings = [''] * count
                    prev_end = 0
                    for s in range(count):
                        start = offsets[s]
                        end = offsets[s + 1]
                        if start < prev_end or end < start or end > tail:
                            continue
                        prev_end = end
                        if end > start:
                            strings[s] = d[str_base + start:str_base + end].decode(
                                'utf-8', errors='replace')
                    pool_cursor += tail
                    group_pool_list.append(strings)
                self._group_pools[g] = group_pool_list

            self._pools_loaded = True

    # PERF-03: Inlined child reads. Called in hot path, so manual inlining avoids
    # method-call + attribute-lookup overhead per bit.
    def _trie_walk_v4(self, ip_int):
        d = self._data
        off_jump = self._off_v4_jump
        off_nodes = self._off_v4_nodes
        v4_node_count = self._v4_node_count
        v4_node_24 = self._v4_node_24

        hi16 = (ip_int >> 16) & 0xFFFF
        ptr = struct.unpack_from('<I', d, off_jump + hi16 * 4)[0]

        if ptr == 0:
            return 0
        if ptr & SENTINEL:
            return ptr & SENTINEL_MASK_31

        idx = ptr
        suffix = (ip_int & 0xFFFF) << 16
        steps = 0

        if v4_node_24:
            while True:
                steps += 1
                if steps >= MAX_TRIE_WALK_STEPS:
                    return 0
                bit = (suffix >> 31) & 1
                if idx >= v4_node_count:
                    return 0
                noff = off_nodes + idx * 6
                off = noff if bit == 0 else noff + 3
                child = d[off] | (d[off + 1] << 8) | (d[off + 2] << 16)
                if child & 0x800000:
                    return child & 0x7FFFFF
                if child == 0:
                    return 0
                idx = child
                suffix <<= 1
        else:
            # 32-bit nodes (8 bytes each: left uint32 + right uint32)
            # bit 31 is sentinel (SENTINEL = 0x80000000)
            unpack_u32 = struct.Struct('<I').unpack_from
            while True:
                steps += 1
                if steps >= MAX_TRIE_WALK_STEPS:
                    return 0
                bit = (suffix >> 31) & 1
                child_off = off_nodes + idx * 8 + bit * 4
                child = unpack_u32(d, child_off)[0]
                if child & SENTINEL:
                    return child & SENTINEL_MASK_31
                if child == 0:
                    return 0
                idx = child
                suffix <<= 1

    def _trie_walk_v6(self, ip_int):
        d = self._data
        off_jump = self._off_v6_jump
        off_nodes = self._off_v6_nodes
        v6_node_count = self._v6_node_count
        v6_node_24 = self._v6_node_24
        jump_bits = self._v6_jump_bits

        shift = 128 - jump_bits
        idx_jump = (ip_int >> shift) & ((1 << jump_bits) - 1)
        ptr = struct.unpack_from('<I', d, off_jump + idx_jump * 4)[0]
        if ptr == 0:
            return 0
        if ptr & SENTINEL:
            return ptr & SENTINEL_MASK_31

        idx = ptr
        depth = jump_bits

        if v6_node_24:
            while depth < 128:
                bit = (ip_int >> (127 - depth)) & 1
                if idx >= v6_node_count:
                    return 0
                noff = off_nodes + idx * 6
                off = noff if bit == 0 else noff + 3
                child = d[off] | (d[off + 1] << 8) | (d[off + 2] << 16)
                if child & 0x800000:
                    return child & 0x7FFFFF
                if child == 0:
                    return 0
                idx = child
                depth += 1
        else:
            unpack_u32 = struct.Struct('<I').unpack_from
            while depth < 128:
                bit = (ip_int >> (127 - depth)) & 1
                child_off = off_nodes + idx * 8 + bit * 4
                child = unpack_u32(d, child_off)[0]
                if child & SENTINEL:
                    return child & SENTINEL_MASK_31
                if child == 0:
                    return 0
                idx = child
                depth += 1
        return 0

    def _read_ip_row(self, row_id):
        if row_id <= 0 or row_id >= self._row_count:
            return 0, 0, 0
        off = self._off_ip_row + row_id * self._ip_row_size
        if self._off_row_schema > 0:
            p = off
            geo_id = self.safe_read_uint_width(p, self._row_geo_width)
            p += self._row_geo_width
            asn_id = 0
            if self._row_asn_width > 0:
                asn_id = self.safe_read_uint_width(p, self._row_asn_width)
                p += self._row_asn_width
            usage_type_id = 0
            if self._row_usage_width > 0:
                usage_type_id = self.safe_read_uint_width(p, self._row_usage_width)
        else:
            geo_id = self.safe_read_u24(off)
            asn_id = self.safe_read_u24(off + 3)
            usage_type_id = self.safe_read_u24(off + 6) if self._ip_row_size >= 9 else 0

        return geo_id, asn_id, usage_type_id

    def _resolve_row_id(self, row_id, group_index):
        geo_id, asn_id, usage_type_id = self._read_ip_row(row_id)
        mask = self._group_dim_masks[group_index] if group_index < len(self._group_dim_masks) else 0

        if mask & 0x02:
            entry_id = asn_id
        elif mask & 0x04:
            entry_id = usage_type_id
        else:
            entry_id = geo_id

        if entry_id == 0:
            return None
        return self._cached_resolve_geo(entry_id, group_index)

    def _cached_resolve_geo(self, entry_id, group_index):
        """Per-snapshot bounded lock-free GeoInfo cache (API contract §3/§9).

        Keyed by ``(group_index, entry_id)``. On a miss we resolve and store the
        GeoInfo (up to ``_geo_cache_max`` entries); once full we simply skip
        storing and recompute. A cached ``None`` is still correct for its key, so
        we never return a value for the wrong key (collision → recompute).
        """
        key = (group_index, entry_id)
        cache = self._geo_cache
        if key in cache:
            return cache[key]
        val = self._resolve_geo(entry_id, group_index)
        if len(cache) < self._geo_cache_max:
            cache[key] = val
        return val

    def _resolve_geo(self, entry_id, group_index):
        if group_index < 0 or group_index >= len(self._group_field_counts):
            return None
        if entry_id < 0 or entry_id >= self._group_entry_counts[group_index]:
            return None

        self._ensure_pools_loaded()

        field_count = self._group_field_counts[group_index]
        if field_count <= 0:
            return None

        group_entry_start = self._off_geo_entries + self._group_entry_offsets[group_index]
        stride = self._group_strides[group_index]
        entry_offset = group_entry_start + entry_id * stride
        d = self._data

        widths = self._group_field_widths[group_index]
        base_offsets = self._group_field_offsets[group_index]
        natives = self._group_field_native[group_index]
        nat_types = self._group_field_native_type[group_index]

        values = []
        for i in range(field_count):
            w = widths[i]
            fo = entry_offset + base_offsets[i]
            is_native = natives and i < len(natives) and natives[i]
            
            if is_native:
                t = nat_types[i] if nat_types and i < len(nat_types) else 0
                val = self._decode_native(t, w, fo)
            else:
                idx = self.safe_read_uint_width(fo, w)
                group_pool = self._group_pools[group_index]
                if group_pool and i < len(group_pool) and idx < len(group_pool[i]):
                    val = group_pool[i][idx]
                else:
                    val = ''

            values.append(val)

        # Always use *this group's* names — groups can have different field
        # counts, so borrowing the active group's table would mislabel values.
        g_names = self._group_field_names[group_index] or self._field_names
        g_name_idx = self._group_name_idx[group_index] or self._name_idx
        g_floats = self._group_float_indices[group_index]
        if g_floats is None:
            g_floats = self._float_field_indices
        return GeoInfo(values=values, field_names=g_names,
                       float_indices=g_floats,
                       name_idx=g_name_idx)

    # ── bytes-based IPv6 helpers ──────────────────────────────────────

    def _trie_walk_v6_bytes(self, ip_bytes):
        d = self._data
        off_jump = self._off_v6_jump
        off_nodes = self._off_v6_nodes
        v6_node_count = self._v6_node_count
        v6_node_24 = self._v6_node_24
        jump_bits = self._v6_jump_bits

        shift = 128 - jump_bits
        if jump_bits <= 64:
            hi = (ip_bytes[0] << 56) | (ip_bytes[1] << 48) | (ip_bytes[2] << 40) | (ip_bytes[3] << 32) | (ip_bytes[4] << 24) | (ip_bytes[5] << 16) | (ip_bytes[6] << 8) | ip_bytes[7]
            idx_jump = (hi >> (64 - jump_bits)) & ((1 << jump_bits) - 1)
        else:
            full = int.from_bytes(ip_bytes, 'big')
            idx_jump = (full >> shift) & ((1 << jump_bits) - 1)
        ptr = struct.unpack_from('<I', d, off_jump + idx_jump * 4)[0]
        if ptr == 0:
            return 0
        if ptr & SENTINEL:
            return ptr & SENTINEL_MASK_31

        idx = ptr
        depth = jump_bits

        if v6_node_24:
            while depth < 128:
                bit = (ip_bytes[depth >> 3] >> (7 - (depth & 7))) & 1
                if idx >= v6_node_count:
                    return 0
                noff = off_nodes + idx * 6
                off = noff if bit == 0 else noff + 3
                child = d[off] | (d[off + 1] << 8) | (d[off + 2] << 16)
                if child & 0x800000:
                    return child & 0x7FFFFF
                if child == 0:
                    return 0
                idx = child
                depth += 1
        else:
            unpack_u32 = struct.Struct('<I').unpack_from
            while depth < 128:
                bit = (ip_bytes[depth >> 3] >> (7 - (depth & 7))) & 1
                child_off = off_nodes + idx * 8 + bit * 4
                child = unpack_u32(d, child_off)[0]
                if child & SENTINEL:
                    return child & SENTINEL_MASK_31
                if child == 0:
                    return 0
                idx = child
                depth += 1
        return 0

    # ── find / lookup ────────────────────────────────────────────────

    def find(self, ip_str):
        if self._closed:
            return None
        if not ip_str:
            raise QzdbError(f'invalid IP address: {ip_str!r}', QzdbError.INVALID_PARAM)
        parsed = _fast_parse_ip(ip_str)
        if parsed is None:
            raise QzdbError(f'invalid IP address: {ip_str!r}', QzdbError.INVALID_PARAM)
        v4, v6 = parsed
        if v4 is not None:
            return self.find_uint(v4)
        return self.find_v6_bytes(v6)

    def find_uint(self, ip_int):
        if self._closed:
            return None
        if not self._has_v4:
            return None
        row_id = self._trie_walk_v4(ip_int)
        if row_id == 0:
            return None
        return self._resolve_row_id(row_id & SENTINEL_MASK_31, self._group_index)

    def find_v6_bytes(self, ip_bytes):
        """IPv6 lookup using 16-byte packed representation (zero BigInteger alloc).

        An IPv4-mapped IPv6 (``::ffff:a.b.c.d``) is downgraded to the V4 trie,
        matching ``find`` / ``find_bytes`` normalization (API contract §5.3).
        """
        if self._closed:
            return None
        if not self._has_v6:
            return None
        if len(ip_bytes) == 16 and ip_bytes[10] == 0xFF and ip_bytes[11] == 0xFF:
            # Check that bytes 0..9 are all zero → genuine v4-mapped address.
            if ip_bytes[:10] == b'\x00' * 10:
                v4 = ((ip_bytes[12] & 0xFF) << 24 | (ip_bytes[13] & 0xFF) << 16
                      | (ip_bytes[14] & 0xFF) << 8 | (ip_bytes[15] & 0xFF))
                return self.find_uint(v4)
        row_id = self._trie_walk_v6_bytes(ip_bytes)
        if row_id == 0:
            return None
        # FIX: strip sentinel bit (same as find_uint for V4)
        return self._resolve_row_id(row_id & SENTINEL_MASK_31, self._group_index)

    def find_v6_uint(self, ip_int):
        if self._closed:
            return None
        if not self._has_v6:
            return None
        row_id = self._trie_walk_v6(ip_int)
        if row_id == 0:
            return None
        # FIX: strip sentinel bit (same as find_uint for V4)
        return self._resolve_row_id(row_id & SENTINEL_MASK_31, self._group_index)

    def find_bytes(self, ip_bytes):
        """Lookup by raw address bytes: 4 bytes → IPv4, 16 bytes → IPv6.

        An IPv4-mapped IPv6 (``::ffff:a.b.c.d``) is downgraded to the V4 trie.
        Returns ``None`` on no match, closed reader, or invalid length.
        """
        if self._closed:
            return None
        if ip_bytes is None:
            return None
        if len(ip_bytes) == 4:
            v4 = ((ip_bytes[0] & 0xFF) << 24 | (ip_bytes[1] & 0xFF) << 16
                  | (ip_bytes[2] & 0xFF) << 8 | (ip_bytes[3] & 0xFF))
            return self.find_uint(v4)
        if len(ip_bytes) == 16:
            if (ip_bytes[10] == 0xFF and ip_bytes[11] == 0xFF
                    and ip_bytes[0] == 0 and ip_bytes[1] == 0 and ip_bytes[2] == 0
                    and ip_bytes[3] == 0 and ip_bytes[4] == 0 and ip_bytes[5] == 0
                    and ip_bytes[6] == 0 and ip_bytes[7] == 0 and ip_bytes[8] == 0
                    and ip_bytes[9] == 0):
                v4 = ((ip_bytes[12] & 0xFF) << 24 | (ip_bytes[13] & 0xFF) << 16
                      | (ip_bytes[14] & 0xFF) << 8 | (ip_bytes[15] & 0xFF))
                return self.find_uint(v4)
            return self.find_v6_bytes(ip_bytes)
        return None

    # ── field projection (only resolve requested fields) ─────────────

    def _resolve_geo_fields(self, entry_id, group_index, field_indices):
        if group_index < 0 or group_index >= len(self._group_field_counts):
            return [], []
        if entry_id < 0 or entry_id >= self._group_entry_counts[group_index]:
            return [], []
        self._ensure_pools_loaded()
        field_count = self._group_field_counts[group_index]
        if field_count <= 0:
            return [], []
        group_entry_start = self._off_geo_entries + self._group_entry_offsets[group_index]
        stride = self._group_strides[group_index]
        entry_offset = group_entry_start + entry_id * stride
        d = self._data
        widths = self._group_field_widths[group_index]
        base_offsets = self._group_field_offsets[group_index]
        natives = self._group_field_native[group_index]
        nat_types = self._group_field_native_type[group_index]
        values = []
        resolved_names = []
        for i in field_indices:
            if i < 0 or i >= field_count:
                values.append('')
                resolved_names.append(f'field_{i}')
                continue
            w = widths[i]
            fo = entry_offset + base_offsets[i]
            is_native = natives and i < len(natives) and natives[i]
            if is_native:
                t = nat_types[i] if nat_types and i < len(nat_types) else 0
                val = self._decode_native(t, w, fo)
            else:
                idx = self.safe_read_uint_width(fo, w)
                group_pool = self._group_pools[group_index]
                if group_pool and i < len(group_pool) and idx < len(group_pool[i]):
                    val = group_pool[i][idx]
                else:
                    val = ''
            values.append(val)
            resolved_names.append(self._field_names[i] if i < len(self._field_names) else f'field_{i}')
        return values, resolved_names

    def find_fields(self, ip_str, field_names=None):
        """Field-projection query (API contract §9.6).

        Returns a GeoInfo with *only* the requested fields, in the requested
        order. Unknown fields yield ``""`` (preserving input/output length
        parity). Returns ``None`` only when the IP itself is not found.
        """
        if self._closed:
            return None
        if field_names is None:
            return self.find(ip_str)
        if not ip_str:
            return None
        parsed = _fast_parse_ip(ip_str)
        if parsed is None:
            return None
        v4, v6 = parsed
        if v4 is not None:
            row_id = self._trie_walk_v4(v4)
        else:
            row_id = self._trie_walk_v6_bytes(v6)
        if row_id == 0:
            return None
        row_id &= SENTINEL_MASK_31
        geo_id, asn_id, usage_type_id = self._read_ip_row(row_id)
        mask = self._group_dim_masks[self._group_index] if self._group_index < len(self._group_dim_masks) else 0
        entry_id = asn_id if (mask & 0x02) else (usage_type_id if (mask & 0x04) else geo_id)
        if entry_id == 0:
            return None
        # Use the same pre-built normalized index as get()/hasField() so that
        # field-name matching is case/underscore/hyphen-insensitive (§6.1).
        indices = []
        for n in field_names:
            idx = self._norm_idx.get(_norm_key(n))
            indices.append(idx if idx is not None else -1)
        values, resolved_names = self._resolve_geo_fields(entry_id, self._group_index, indices)
        return GeoInfo(values=values, field_names=resolved_names,
                       float_indices=self._float_field_indices)

    # ── lookup row id / ids ──────────────────────────────────────────

    def lookup_row_id(self, ip_str):
        if self._closed:
            return 0
        if not ip_str:
            return 0
        parsed = _fast_parse_ip(ip_str)
        if parsed is None:
            return 0
        v4, v6 = parsed
        if v4 is not None:
            return self.lookup_row_id_uint(v4)
        return self.lookup_row_id_v6_bytes(v6)

    def lookup_row_id_uint(self, ip_int):
        if self._closed or not self._has_v4:
            return 0
        # Strip the sentinel bit so the returned value is a clean 0-based row_id
        # (0 = not found). Mirrors find_uint(), which strips before _resolve_row_id.
        # Without this, callers like lookup_ids()/find_fields() see a huge value
        # (row_id | 0x80000000) and treat it as out-of-bounds → (0,0,0).
        rid = self._trie_walk_v4(ip_int)
        return (rid & SENTINEL_MASK_31) if rid != 0 else 0

    def lookup_row_id_v6(self, ip_int):
        if self._closed or not self._has_v6:
            return 0
        rid = self._trie_walk_v6(ip_int)
        return (rid & SENTINEL_MASK_31) if rid != 0 else 0

    def lookup_row_id_v6_bytes(self, ip_bytes):
        if self._closed or not self._has_v6:
            return 0
        rid = self._trie_walk_v6_bytes(ip_bytes)
        return (rid & SENTINEL_MASK_31) if rid != 0 else 0

    def lookup_ids(self, row_id):
        if self._closed:
            return None
        if row_id <= 0 or row_id >= self._row_count:
            return None
        return RowIds(*self._read_ip_row(row_id))

    def find_str(self, ip_str):
        if self._closed:
            return ''
        try:
            info = self.find(ip_str)
        except QzdbError:
            return ''
        if info is None:
            return ''
        return info.to_pipe()

    # ── CIDR reverse lookup (API contract §5 / §8.6) ───────────────
    # The DB does not store CIDR; the most-specific network is rebuilt from the
    # trie leaf depth (= prefix length N), with the network address = the IP's
    # top N bits zeroed. A jump-table hit that is itself a leaf yields prefix =
    # the jump bits already consumed from the root (never a wrong network).

    def _cidr_walk_v4(self, ip_int):
        d = self._data
        off_jump = self._off_v4_jump
        off_nodes = self._off_v4_nodes
        v4_node_24 = self._v4_node_24
        hi16 = (ip_int >> 16) & 0xFFFF
        ptr = struct.unpack_from('<I', d, off_jump + hi16 * 4)[0]
        if ptr == 0:
            return 0, 0
        if ptr & SENTINEL:
            return ptr & SENTINEL_MASK_31, 16
        idx = ptr
        suffix = (ip_int & 0xFFFF) << 16
        steps = 0
        if v4_node_24:
            while True:
                steps += 1
                if steps > 16:
                    return 0, 0
                bit = (suffix >> 31) & 1
                if idx >= self._v4_node_count:
                    return 0, 0
                noff = off_nodes + idx * 6
                off = noff if bit == 0 else noff + 3
                child = d[off] | (d[off + 1] << 8) | (d[off + 2] << 16)
                if child & 0x800000:
                    return child & 0x7FFFFF, 16 + steps
                if child == 0:
                    return 0, 0
                idx = child
                suffix <<= 1
        else:
            while True:
                steps += 1
                if steps > 16:
                    return 0, 0
                bit = (suffix >> 31) & 1
                child = struct.unpack_from('<I', d, off_nodes + idx * 8 + bit * 4)[0]
                if child & SENTINEL:
                    return child & SENTINEL_MASK_31, 16 + steps
                if child == 0:
                    return 0, 0
                idx = child
                suffix <<= 1

    def _cidr_walk_v6(self, ip_int):
        d = self._data
        off_jump = self._off_v6_jump
        off_nodes = self._off_v6_nodes
        v6_node_24 = self._v6_node_24
        jump_bits = self._v6_jump_bits
        shift = 128 - jump_bits
        idx_jump = (ip_int >> shift) & ((1 << jump_bits) - 1)
        ptr = struct.unpack_from('<I', d, off_jump + idx_jump * 4)[0]
        if ptr == 0:
            return 0, 0
        if ptr & SENTINEL:
            return ptr & SENTINEL_MASK_31, jump_bits
        idx = ptr
        depth = jump_bits
        if v6_node_24:
            while depth < 128:
                bit = (ip_int >> (127 - depth)) & 1
                if idx >= self._v6_node_count:
                    return 0, 0
                noff = off_nodes + idx * 6
                off = noff if bit == 0 else noff + 3
                child = d[off] | (d[off + 1] << 8) | (d[off + 2] << 16)
                if child & 0x800000:
                    return child & 0x7FFFFF, depth + 1
                if child == 0:
                    return 0, 0
                idx = child
                depth += 1
        else:
            while depth < 128:
                bit = (ip_int >> (127 - depth)) & 1
                child = struct.unpack_from('<I', d, off_nodes + idx * 8 + bit * 4)[0]
                if child & SENTINEL:
                    return child & SENTINEL_MASK_31, depth + 1
                if child == 0:
                    return 0, 0
                idx = child
                depth += 1
        return 0, 0

    @staticmethod
    def _format_cidr_v4(ip_int, prefix):
        if prefix >= 32:
            mask = 0xFFFFFFFF
        else:
            mask = (~((1 << (32 - prefix)) - 1)) & 0xFFFFFFFF
        net = ip_int & mask
        return f'{ipaddress.IPv4Address(net)}/{prefix}'

    @staticmethod
    def _format_cidr_v6(ip_int, prefix):
        if prefix >= 128:
            mask = (1 << 128) - 1
        else:
            mask = (~((1 << (128 - prefix)) - 1)) & ((1 << 128) - 1)
        net = ip_int & mask
        return f'{ipaddress.IPv6Address(net)}/{prefix}'

    def lookup_cidr(self, ip_str):
        if self._closed or not ip_str:
            return None
        parsed = _fast_parse_ip(ip_str)
        if parsed is None:
            return None
        v4, v6 = parsed
        if v4 is not None:
            row_id, prefix = self._cidr_walk_v4(v4)
            if row_id == 0:
                return None
            return self._format_cidr_v4(v4, prefix)
        v6_int = int.from_bytes(v6, 'big')
        row_id, prefix = self._cidr_walk_v6(v6_int)
        if row_id == 0:
            return None
        return self._format_cidr_v6(v6_int, prefix)

    def lookup_cidr_uint(self, ip_int):
        if self._closed or not self._has_v4:
            return None
        row_id, prefix = self._cidr_walk_v4(ip_int)
        if row_id == 0:
            return None
        return self._format_cidr_v4(ip_int, prefix)

    def lookup_cidr_bytes(self, ip_bytes):
        if self._closed or ip_bytes is None:
            return None
        if len(ip_bytes) == 4:
            v4 = ((ip_bytes[0] & 0xFF) << 24 | (ip_bytes[1] & 0xFF) << 16
                  | (ip_bytes[2] & 0xFF) << 8 | (ip_bytes[3] & 0xFF))
            return self.lookup_cidr_uint(v4)
        if len(ip_bytes) == 16:
            if (ip_bytes[10] == 0xFF and ip_bytes[11] == 0xFF
                    and ip_bytes[0] == 0 and ip_bytes[1] == 0 and ip_bytes[2] == 0
                    and ip_bytes[3] == 0 and ip_bytes[4] == 0 and ip_bytes[5] == 0
                    and ip_bytes[6] == 0 and ip_bytes[7] == 0 and ip_bytes[8] == 0
                    and ip_bytes[9] == 0):
                v4 = ((ip_bytes[12] & 0xFF) << 24 | (ip_bytes[13] & 0xFF) << 16
                      | (ip_bytes[14] & 0xFF) << 8 | (ip_bytes[15] & 0xFF))
                return self.lookup_cidr_uint(v4)
            v6_int = int.from_bytes(ip_bytes, 'big')
            row_id, prefix = self._cidr_walk_v6(v6_int)
            if row_id == 0:
                return None
            return self._format_cidr_v6(v6_int, prefix)
        return None

    # ── batch / stream (API contract §5) ───────────────────────────

    def find_batch(self, ips):
        """Batch lookup. Per-item three-state semantics preserved; no thread pool.

        ``error`` is populated with a ``QzdbError`` when an input is invalid
        (API contract §8.2), while a clean miss leaves ``error`` as ``None``.
        """
        results = []
        for ip in ips:
            try:
                gi = self.find(ip)
            except QzdbError as e:
                results.append(BatchResult(ip, None, e))
                continue
            results.append(BatchResult(ip, gi, None))
        return results

    def find_batch_fields(self, ips, fields):
        results = []
        for ip in ips:
            try:
                gi = self.find_fields(ip, fields)
            except QzdbError as e:
                results.append(BatchResult(ip, None, e))
                continue
            results.append(BatchResult(ip, gi, None))
        return results

    def find_stream(self, ips):
        """Lazy stream — constant memory, yields each GeoInfo (or None) in turn.

        Invalid inputs yield ``None`` (lenient GeoInfo|None stream). For the
        three-state BatchResult stream, use :meth:`find_iter`.
        """
        for ip in ips:
            try:
                yield self.find(ip)
            except QzdbError:
                yield None

    def find_iter(self, ips):
        """Three-state lazy stream (API contract §8.4).

        Yields a :class:`BatchResult` per input IP, preserving the
        found / not-found / invalid-input distinction via ``geo_info`` / ``error``.
        """
        for ip in ips:
            try:
                gi = self.find(ip)
            except QzdbError as e:
                yield BatchResult(ip, None, e)
                continue
            yield BatchResult(ip, gi, None)

    # ── metadata introspection (API contract §5) ───────────────────

    def get_field_names(self):
        return self._field_names

    def has_field(self, name):
        return _norm_key(name) in self._norm_idx

    def get_group_count(self):
        return len(self._group_field_counts)

    def get_pool_count(self):
        """Number of string pools declared in the header (offset 12)."""
        return self._pool_count

    def get_edition(self):
        return self._edition

    def get_version_mask(self):
        """Raw ``Header.VersionMask`` (offset 6) — the one-hot edition bitmask.

        bit0=std, bit1=asn, bit2=pro, bit3=max, bit4=ult (FORMAT §3.1).
        """
        return self._version_mask

    def get_edition_source(self):
        """How get_edition() was resolved: version_mask|metadata|inferred|unknown."""
        return self._edition_source

    def get_field_names_source(self):
        """How get_field_names() was resolved: metadata|edition|synthetic.

        ``metadata`` means the names were read from the file. ``edition`` means
        the file omitted them and the SDK supplied the canonical set for its
        declared edition. ``synthetic`` means neither was available and the
        names are positional placeholders, so name-based lookup is meaningless.
        """
        return self._field_names_source

    def get_scope(self):
        return ''

    def get_description(self):
        return self._description

    def get_build_time(self):
        if self._build_date > 0:
            y = self._build_date // 10000
            m = (self._build_date // 100) % 100
            d = self._build_date % 100
            return f'{y:04d}-{m:02d}-{d:02d}'
        return ''

    def get_data_month(self):
        if self._build_date > 0:
            y = self._build_date // 10000
            m = (self._build_date // 100) % 100
            return f'{y:04d}-{m:02d}'
        return ''

    def get_file_hash(self):
        d = self._data
        if len(d) < 20:
            return ''
        stored = struct.unpack_from('<I', d, 16)[0]
        return f'{stored:08x}'

    def get_version(self):
        return self._version_name

    @property
    def version(self):
        return self._version_name

    @property
    def field_names(self):
        return self._field_names

    @property
    def version_code(self):
        pc_map = {6: 1, 7: 2, 25: 3}
        return pc_map.get(self._pool_count, 3)

    @property
    def pool_count(self):
        return self._pool_count

    def verify_crc(self) -> bool:
        d = self._data
        if len(d) < 20:
            return False
        stored = struct.unpack_from('<I', d, 16)[0]
        # Segmented CRC: CRC field counted as zero — no full-buffer copy
        # Segmented CRC using zlib.crc32 naive chaining.
        # zlib.crc32 already XORs the result with 0xFFFFFFFF (final XOR),
        # so we chain directly: zlib.crc32(part2, zlib.crc32(part1)) == zlib.crc32(part1+part2)
        crc = zlib.crc32(d[:16])
        crc = zlib.crc32(b'\x00' * 4, crc)
        if len(d) > 20:
            crc = zlib.crc32(d[20:], crc)
        return stored == (crc & 0xFFFFFFFF)


# Named result of lookup_ids() — a tuple subclass (API contract appendix A.5).
# Still a tuple, so callers that unpack ``geo_id, asn_id, usage_type_id = ...``
# keep working, while named access (``row.geo_id``) is also available.
RowIds = namedtuple('RowIds', ['geo_id', 'asn_id', 'usage_type_id'])


class BatchResult:
    """Result of a single lookup in a batch/stream (API contract §1).

    Attributes:
        ip: the input IP string.
        geo_info: a ``GeoInfo`` on hit, else ``None``.
        error: a ``QzdbError`` if the lookup raised, else ``None``.
    """

    __slots__ = ('ip', 'geo_info', 'error')

    def __init__(self, ip, geo_info=None, error=None):
        self.ip = ip
        self.geo_info = geo_info
        self.error = error

    @property
    def info(self):
        """Spec-named alias of ``geo_info`` (API contract appendix A.5)."""
        return self.geo_info

    def __repr__(self):
        if self.error is not None:
            return f'BatchResult(ip={self.ip!r}, error={self.error!r})'
        if self.geo_info is None:
            return f'BatchResult(ip={self.ip!r}, geo_info=None)'
        return f'BatchResult(ip={self.ip!r}, geo_info={self.geo_info.to_pipe()!r})'


class QzdbRegistry:
    """Multi-database registry (API contract §1 / §5).

    Holds several named ``QzdbReader`` instances and answers a query by trying
    each registered reader in insertion order, returning the first hit. Useful
    when one logical service spans multiple physical DB editions/groups.
    """

    def __init__(self):
        self._readers = {}

    def register(self, name, reader):
        if not isinstance(reader, QzdbReader):
            raise QzdbError('registry entry must be a QzdbReader', QzdbError.INVALID_PARAM)
        self._readers[name] = reader
        return self

    def get(self, name):
        return self._readers.get(name)

    def names(self):
        return list(self._readers.keys())

    def find(self, ip_str):
        for r in self._readers.values():
            gi = r.find(ip_str)
            if gi is not None:
                return gi
        return None

    def find_uint(self, ip_int):
        for r in self._readers.values():
            gi = r.find_uint(ip_int)
            if gi is not None:
                return gi
        return None

    def find_bytes(self, ip_bytes):
        for r in self._readers.values():
            gi = r.find_bytes(ip_bytes)
            if gi is not None:
                return gi
        return None

    def find_fields(self, ip_str, fields=None):
        for r in self._readers.values():
            gi = r.find_fields(ip_str, fields)
            if gi is not None:
                return gi
        return None

    def lookup_row_id(self, ip_str):
        for r in self._readers.values():
            rid = r.lookup_row_id(ip_str)
            if rid != 0:
                return rid
        return 0

    def lookup_cidr(self, ip_str):
        for r in self._readers.values():
            c = r.lookup_cidr(ip_str)
            if c is not None:
                return c
        return None

    def close(self):
        for r in self._readers.values():
            r.close()

    # ── spec-named registry management (API contract §9.5) ──────────

    def register_path(self, name, path, **kwargs):
        """Load a DB from a file path and register it under ``name``."""
        reader = QzdbReader(path, **kwargs)
        self._readers[name] = reader
        return self

    def register_buffer(self, name, buffer, **kwargs):
        """Load a DB from an in-memory buffer and register it under ``name``."""
        reader = QzdbReader.open_buffer(buffer, **kwargs)
        self._readers[name] = reader
        return self

    def unregister(self, name):
        """Remove a registered reader (no-op if absent)."""
        self._readers.pop(name, None)
        return self

    def find_batch(self, ips):
        """Per-IP first-hit across registered readers (three-state BatchResult)."""
        results = []
        for ip in ips:
            gi = None
            err = None
            for r in self._readers.values():
                try:
                    gi = r.find(ip)
                except QzdbError as e:
                    err = e
                    gi = None
                if gi is not None:
                    break
            results.append(BatchResult(ip, gi, err))
        return results


# API-contract name for the registry class (appendix A.5).
Registry = QzdbRegistry


class ChainedReader:
    """Chained multi-database reader (API contract §1 / §5).

    Wraps an ordered list of ``QzdbReader`` instances; a query returns the first
    non-``None`` result. Optionally a per-reader dimension mask can be supplied to
    restrict which dimension each reader answers for, but by default every
    reader is tried for every dimension.
    """

    def __init__(self, readers, masks=None):
        self._readers = list(readers) if readers else []
        self._masks = list(masks) if masks else None

    def add(self, reader, mask=None):
        if not isinstance(reader, QzdbReader):
            raise QzdbError('chained entry must be a QzdbReader', QzdbError.INVALID_PARAM)
        self._readers.append(reader)
        if self._masks is not None:
            self._masks.append(mask)
        return self

    def find(self, ip_str):
        for r in self._readers:
            gi = r.find(ip_str)
            if gi is not None:
                return gi
        return None

    def find_uint(self, ip_int):
        for r in self._readers:
            gi = r.find_uint(ip_int)
            if gi is not None:
                return gi
        return None

    def find_bytes(self, ip_bytes):
        for r in self._readers:
            gi = r.find_bytes(ip_bytes)
            if gi is not None:
                return gi
        return None

    def find_fields(self, ip_str, fields=None):
        for r in self._readers:
            gi = r.find_fields(ip_str, fields)
            if gi is not None:
                return gi
        return None

    def lookup_row_id(self, ip_str):
        for r in self._readers:
            rid = r.lookup_row_id(ip_str)
            if rid != 0:
                return rid
        return 0

    def lookup_cidr(self, ip_str):
        for r in self._readers:
            c = r.lookup_cidr(ip_str)
            if c is not None:
                return c
        return None

    def find_batch(self, ips):
        """Per-IP first-hit across chained readers (three-state BatchResult)."""
        results = []
        for ip in ips:
            gi = None
            err = None
            for r in self._readers:
                try:
                    gi = r.find(ip)
                except QzdbError as e:
                    err = e
                    gi = None
                if gi is not None:
                    break
            results.append(BatchResult(ip, gi, err))
        return results

    # ── spec-named chain factories (API contract §9.5) ──────────────

    @staticmethod
    def chain(*readers):
        """Build a ChainedReader from the given readers (first hit wins)."""
        return ChainedReader(list(readers))

    @staticmethod
    def chain_merge(*readers):
        """Alias of :meth:`chain` — merge readers, first (earliest) hit wins."""
        return ChainedReader(list(readers))

    @staticmethod
    def chain_merge_override(*readers):
        """Merge readers with later readers taking priority on conflict.

        Implemented by reversing the resolution order so a later reader's hit
        shadows an earlier one's.
        """
        return ChainedReader(list(reversed(readers)))

    @property
    def readers(self):
        """The underlying ordered reader list (API contract §9.3)."""
        return list(self._readers)

    @property
    def editions(self):
        """Per-reader edition strings, in chain order (API contract §9.3)."""
        return [r.get_edition() for r in self._readers]

    @property
    def scopes(self):
        """Per-reader scope strings, in chain order (API contract §9.3)."""
        return [r.get_scope() for r in self._readers]

    def close(self):
        for r in self._readers:
            r.close()
