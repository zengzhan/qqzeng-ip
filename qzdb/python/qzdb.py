import struct
import zlib
import threading

SENTINEL = 0x80000000
SENTINEL_MASK_24 = 0x7FFFFF
SENTINEL_MASK_31 = 0x7FFFFFFF
MAX_TRIE_WALK_STEPS = 1000
FLOAT_FIELDS = frozenset(['longitude', 'latitude'])

# ── strict IP parsing (SEC-05 / CODE-03) ───────────────────────────

_HEX = {}
for _i in range(10):
    _HEX[chr(48 + _i)] = _i
for _i in range(6):
    _HEX[chr(97 + _i)] = 10 + _i
    _HEX[chr(65 + _i)] = 10 + _i


def _fast_parse_ip(s):
    """Parse IP string with strict validation (SEC-05).
    Returns (v4_int, None) for IPv4 or (None, v6_bytes) for IPv6.
    Returns None for invalid input. Trims whitespace. Max length 45.
    """
    s = s.strip()
    n = len(s)
    if n == 0 or n > 45:
        return None
    if ':' not in s:
        return _fast_parse_ipv4(s)
    return _fast_parse_ipv6(s)


def _fast_parse_ipv4(s):
    """Strict IPv4 parse. Returns (uint32, None) or None.
    Exactly 4 dot-separated segments, no leading zeros, each 0-255,
    no trailing dot, no empty segments, no port suffix.
    """
    if s[-1] == '.':
        return None
    parts = s.split('.')
    if len(parts) != 4:
        return None
    ip = 0
    for p in parts:
        pl = len(p)
        if pl == 0 or pl > 3:
            return None
        if pl > 1 and p[0] == '0':
            return None
        v = 0
        for c in p:
            if c < '0' or c > '9':
                return None
            v = v * 10 + (ord(c) - 48)
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
            if c not in _HEX:
                return None
    zeros = 8 - ng - v4_slots
    if zeros < 0:
        return None
    buf = bytearray(16)
    off = 0
    for g in lg:
        v = 0
        for c in g:
            v = (v << 4) | _HEX[c]
        buf[off] = v >> 8
        buf[off + 1] = v & 0xFF
        off += 2
    off += zeros * 2
    for g in rg:
        v = 0
        for c in g:
            v = (v << 4) | _HEX[c]
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
    __slots__ = ('_fields', '_field_names', '_float_indices')

    def __init__(self, fields=None, field_names=None, float_indices=None):
        self._fields = fields or {}
        self._field_names = field_names or []
        self._float_indices = set()
        if field_names and float_indices:
            self._float_indices = {field_names[i] for i in float_indices if i < len(field_names)}

    def __getattr__(self, name):
        try:
            return self._fields[name]
        except KeyError:
            raise AttributeError(name)

    def get(self, name):
        return self._fields.get(name, '')

    def to_dict(self):
        return {fname: self._fields.get(fname, '') for fname in self._field_names}

    def to_pipe(self):
        parts = []
        for fname in self._field_names:
            val = self._fields.get(fname, '')
            if fname in self._float_indices and val != '':
                try:
                    val = f'{float(val):.6f}'
                except (ValueError, TypeError):
                    pass
            parts.append(str(val))
        return '|'.join(parts)


class QzdbSearcher:
    _instance = None
    _lock = threading.Lock()

    @classmethod
    def get_instance(cls, db_path=None):
        if cls._instance is None:
            with cls._lock:
                if cls._instance is None:
                    cls._instance = cls(db_path)
        elif db_path is not None:
            with cls._lock:
                cls._instance.load(db_path)
        return cls._instance

    def __init__(self, db_path=None, group_index=0):
        self._data = b''
        self._group_index = group_index
        self._field_names = []
        self._float_field_indices = set()
        self._version_name = ''

        # Header fields
        self._flags = 0
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

    def load(self, db_path):
        try:
            with open(db_path, 'rb') as f:
                self._data = f.read()
        except FileNotFoundError:
            raise QzdbError(f'Database file not found: {db_path}', QzdbError.NOT_FOUND)
        except OSError as exc:
            raise QzdbError(f'Failed to read database file: {exc}', QzdbError.CORRUPTED) from exc
        self._parse_header()

    def _read_u16(self, off):
        return struct.unpack_from('<H', self._data, off)[0]

    def _read_u32(self, off):
        return struct.unpack_from('<I', self._data, off)[0]

    def _read_u64(self, off):
        return struct.unpack_from('<Q', self._data, off)[0]

    def _read_u24(self, off):
        d = self._data
        return d[off] | (d[off + 1] << 8) | (d[off + 2] << 16)

    def _read_u48(self, off):
        d = self._data
        return (d[off]
                | (d[off + 1] << 8)
                | (d[off + 2] << 16)
                | (d[off + 3] << 24)
                | (d[off + 4] << 32)
                | (d[off + 5] << 40))

    def _read_uint_width(self, off, width):
        if width <= 1:
            return self._data[off]
        elif width == 2:
            return self._read_u16(off)
        elif width == 3:
            return self._read_u24(off)
        else:
            return self._read_u32(off)

    def _parse_header(self):
        d = self._data
        if len(d) < 192:
            raise QzdbError('File too small for QZDB header', QzdbError.CORRUPTED)

        magic = d[:4]
        if magic != b'QZDB':
            raise QzdbError('Invalid magic, expected QZDB', QzdbError.BAD_MAGIC)

        fmt_ver = d[4]
        # Accept version 1 (new unified) as well as 2-6 (old V20 development versions)
        if fmt_ver not in (1, 2, 3, 4, 5, 6):
            raise QzdbError(f'Unsupported format version: {fmt_ver}', QzdbError.UNSUPPORTED)

        self._flags = self._read_u16(8)
        self._has_v4 = bool(self._flags & 1)
        self._has_v6 = bool(self._flags & 2)
        self._v4_node_24 = bool(self._flags & 0x10)
        self._v6_node_24 = bool(self._flags & 0x20)

        self._v6_jump_bits = d[11]
        if self._v6_jump_bits == 0:
            self._v6_jump_bits = 16
        if self._v6_jump_bits < 16 or self._v6_jump_bits > 20:
            raise QzdbError(f'v6_jump_bits out of range [16,20]: {self._v6_jump_bits}', QzdbError.INVALID_PARAM)

        self._pool_count = d[12]
        self._pool_idx_size = d[13]
        if self._pool_idx_size not in (2, 3):
            raise QzdbError(f'pool_idx_size must be 2 or 3, got {self._pool_idx_size}', QzdbError.INVALID_PARAM)
        self._geo_count = self._read_u16(14)
        self._row_count = self._read_u32(20)
        self._v4_rec_count = self._read_u32(24)
        self._v6_rec_count = self._read_u32(28)

        hs = self._read_u32(36)
        if hs != 192:
            raise QzdbError(f'Unexpected header size: {hs}', QzdbError.BAD_HEADER)

        # Offsets
        self._off_row_schema = self._read_u64(40)
        self._off_group_schema = self._read_u64(48)
        self._off_v4_jump = self._read_u64(64)
        self._off_v4_nodes = self._read_u64(72)
        self._off_v6_jump = self._read_u64(80)
        self._off_v6_nodes = self._read_u64(88)
        self._off_ip_row = self._read_u64(96)
        self._off_geo_entries = self._read_u64(104)
        self._off_pools = self._read_u64(136)
        self._off_meta = self._read_u64(144)

        self._v4_node_count = self._read_u32(152)
        self._v6_node_count = self._read_u32(156)
        self._ip_row_size = self._read_u32(160)
        if self._ip_row_size < 1 or self._ip_row_size > 64:
            raise QzdbError(f'ip_row_size out of range [1,64]: {self._ip_row_size}', QzdbError.INVALID_PARAM)
        self._geo_entry_group_count = self._read_u32(164)
        if self._geo_entry_group_count < 1 or self._geo_entry_group_count > 255:
            raise QzdbError(f'geo_entry_group_count out of range [1,255]: {self._geo_entry_group_count}', QzdbError.INVALID_PARAM)

        # Bounds validation: raise on corrupt files instead of OOB reads.
        dlen = len(d)
        node_size = 6 if self._v4_node_24 else 8
        v6_node_size = 6 if self._v6_node_24 else 8

        if self._off_v4_jump > 0 and self._off_v4_jump + 65536 * 4 > dlen:
            raise QzdbError('Section v4_jump out of bounds', QzdbError.CORRUPTED)
        if self._off_v4_nodes > 0 and self._off_v4_nodes + self._v4_node_count * node_size > dlen:
            raise QzdbError('Section v4_nodes out of bounds', QzdbError.CORRUPTED)
        if self._off_v6_jump > 0 and self._off_v6_jump + (1 << self._v6_jump_bits) * 4 > dlen:
            raise QzdbError('Section v6_jump out of bounds', QzdbError.CORRUPTED)
        if self._off_v6_nodes > 0 and self._off_v6_nodes + self._v6_node_count * v6_node_size > dlen:
            raise QzdbError('Section v6_nodes out of bounds', QzdbError.CORRUPTED)
        if self._off_ip_row > 0 and self._off_ip_row + self._row_count * self._ip_row_size > dlen:
            raise QzdbError('Section ip_row out of bounds', QzdbError.CORRUPTED)
        if self._off_geo_entries > 0 and self._off_geo_entries >= dlen:
            raise QzdbError('Section geo_entries out of bounds', QzdbError.CORRUPTED)
        if self._off_pools > 0 and self._off_pools >= dlen:
            raise QzdbError('Section pools out of bounds', QzdbError.CORRUPTED)
        if self._off_meta > 0 and self._off_meta > dlen:
            raise QzdbError('Section meta out of bounds', QzdbError.CORRUPTED)

        # GeoEntryOffsets[4]
        self._group_entry_offsets = []
        for i in range(4):
            self._group_entry_offsets.append(self._read_u48(168 + i * 6))

        # Parse GroupMetadataTable (at off_geo_entries)
        gm_off = self._off_geo_entries
        group_count = d[gm_off]
        gm_off += 1

        actual_groups = min(group_count, max(1, self._geo_entry_group_count))
        if actual_groups > 4:
            actual_groups = 4
        self._group_field_counts = [0] * actual_groups
        self._group_entry_counts = [0] * actual_groups
        self._group_dim_masks = [0] * actual_groups

        for gi in range(actual_groups):
            self._group_field_counts[gi] = d[gm_off]
            gm_off += 1
            if fmt_ver == 1 or fmt_ver >= 4:
                self._group_entry_counts[gi] = self._read_u32(gm_off)
                gm_off += 4
            else:
                self._group_entry_counts[gi] = self._read_u16(gm_off)
                gm_off += 2
            
            if fmt_ver == 1 or fmt_ver >= 3:
                self._group_dim_masks[gi] = self._read_u16(gm_off)
                gm_off += 2
            else:
                self._group_dim_masks[gi] = 0x01 if gi != 2 else 0x02

        # Initialize schema and widths
        self._group_strides = [0] * actual_groups
        self._group_field_widths = [None] * actual_groups
        self._group_field_offsets = [None] * actual_groups
        self._group_field_native = [None] * actual_groups
        self._group_field_native_type = [None] * actual_groups

        # Parse GROUP_SCHEMA if present
        if self._off_group_schema > 0:
            sp = self._off_group_schema
            gs_group_count = self._read_u16(sp)
            sp += 2
            max_gs_groups = min(gs_group_count, actual_groups)
            for gi in range(max_gs_groups):
                sp += 2  # skip groupId
                fld_count = self._read_u16(sp)
                sp += 2
                sp += 4  # skip entryCount (uint32)
                stride = self._read_u32(sp)
                sp += 4
                sp += 4  # skip flags

                if gi < actual_groups:
                    self._group_strides[gi] = stride
                    widths = [0] * fld_count
                    offsets = [0] * fld_count
                    natives = [False] * fld_count
                    nat_types = [0] * fld_count
                    for fi in range(fld_count):
                        sp += 2  # skip fieldId
                        widths[fi] = d[sp]
                        sp += 1
                        field_flags = d[sp]
                        sp += 1
                        natives[fi] = (field_flags & 0x01) != 0
                        nat_types[fi] = (field_flags >> 1) & 0x03
                        offsets[fi] = self._read_u32(sp)
                        sp += 4
                        sp += 4  # skip poolSectionId
                    self._group_field_widths[gi] = widths
                    self._group_field_offsets[gi] = offsets
                    self._group_field_native[gi] = natives
                    self._group_field_native_type[gi] = nat_types
                else:
                    sp += fld_count * 12

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

    def _resolve_field_names(self):
        d = self._data
        off_meta = self._off_meta
        if (self._flags & 4) and off_meta > 0 and off_meta + 4 <= len(d):
            field_names = None
            pos = off_meta
            while pos + 4 <= len(d):
                t = d[pos]
                length = self._read_u16(pos + 2)
                if t == 0 or length == 0:
                    break
                val = d[pos + 4:pos + 4 + length].decode('utf-8')
                if t == 1:
                    self._version_name = val
                elif t == 2:
                    field_names = val.split('|')
                pos += 4 + length

            if field_names and len(field_names) == self._group_field_counts[0]:
                self._field_names = field_names
                self._float_field_indices = {
                    i for i, n in enumerate(field_names)
                    if n in FLOAT_FIELDS
                }
                return

        # Fallback placeholder names
        self._field_names = [f'field_{i}' for i in range(self._group_field_counts[0])]
        self._float_field_indices = set()

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
                    count = self._read_u32(pool_cursor)
                    pool_cursor += 4
                    if self._off_row_schema > 0:
                        pool_cursor += 4
                    if count == 0:
                        group_pool_list.append([])
                        continue

                    # Read string offsets
                    offsets = []
                    for _ in range(count + 1):
                        offsets.append(self._read_u32(pool_cursor))
                        pool_cursor += 4

                    # Read string data
                    strings = [''] * count
                    for s in range(count):
                        start = offsets[s]
                        end = offsets[s + 1]
                        length = end - start
                        if length > 0:
                            strings[s] = d[pool_cursor + start:pool_cursor + end].decode('utf-8')
                        else:
                            strings[s] = ''
                    pool_cursor += offsets[count]
                    group_pool_list.append(strings)
                self._group_pools[g] = group_pool_list

            self._pools_loaded = True

    def _get_v4_child(self, node_idx, bit):
        if node_idx >= self._v4_node_count:
            return 0
        if self._v4_node_24:
            node_offset = self._off_v4_nodes + node_idx * 6
            offset = node_offset if bit == 0 else node_offset + 3
            val = self._data[offset] | (self._data[offset + 1] << 8) | (self._data[offset + 2] << 16)
            if val & 0x800000:
                return (val & SENTINEL_MASK_24) | SENTINEL
            return val
        else:
            child_off = self._off_v4_nodes + node_idx * 8 + bit * 4
            return self._read_u32(child_off)

    def _get_v6_child(self, node_idx, bit):
        if node_idx >= self._v6_node_count:
            return 0
        if self._v6_node_24:
            node_offset = self._off_v6_nodes + node_idx * 6
            offset = node_offset if bit == 0 else node_offset + 3
            val = self._data[offset] | (self._data[offset + 1] << 8) | (self._data[offset + 2] << 16)
            if val & 0x800000:
                return (val & SENTINEL_MASK_24) | SENTINEL
            return val
        else:
            child_off = self._off_v6_nodes + node_idx * 8 + bit * 4
            return self._read_u32(child_off)

    def _trie_walk_v4(self, ip_int):
        d = self._data
        hi16 = (ip_int >> 16) & 0xFFFF
        ptr = self._read_u32(self._off_v4_jump + hi16 * 4)

        if ptr == 0:
            return 0
        if ptr & SENTINEL:
            return ptr & SENTINEL_MASK_31

        idx = ptr
        suffix = (ip_int & 0xFFFF) << 16
        steps = 0

        while True:
            steps += 1
            if steps >= MAX_TRIE_WALK_STEPS:
                return 0
            bit = (suffix >> 31) & 1
            child = self._get_v4_child(idx, bit)

            if child == 0:
                return 0
            if child & SENTINEL:
                return child & SENTINEL_MASK_31

            idx = child
            suffix <<= 1

    def _trie_walk_v6(self, ip_int):
        shift = 128 - self._v6_jump_bits
        idx_jump = (ip_int >> shift) & ((1 << self._v6_jump_bits) - 1)
        ptr = self._read_u32(self._off_v6_jump + idx_jump * 4)

        if ptr == 0:
            return 0
        if ptr & SENTINEL:
            return ptr & SENTINEL_MASK_31

        idx = ptr
        depth = self._v6_jump_bits

        while depth < 128:
            bit = (ip_int >> (127 - depth)) & 1
            child = self._get_v6_child(idx, bit)

            if child == 0:
                return 0
            if child & SENTINEL:
                return child & SENTINEL_MASK_31

            idx = child
            depth += 1

        return 0

    def _read_ip_row(self, row_id):
        if row_id <= 0 or row_id >= self._row_count:
            return 0, 0, 0
        off = self._off_ip_row + row_id * self._ip_row_size
        geo_id = self._read_u24(off)
        asn_id = self._read_u24(off + 3)

        usage_type_id = 0
        if self._ip_row_size >= 9:
            usage_type_id = self._read_u24(off + 6)

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
        return self._resolve_geo(entry_id, group_index)

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

        fields = {}
        for i in range(field_count):
            w = widths[i]
            fo = entry_offset + base_offsets[i]
            is_native = natives and i < len(natives) and natives[i]
            
            if is_native:
                t = nat_types[i] if nat_types and i < len(nat_types) else 0
                if t == 1:
                    # float
                    if w == 4:
                        val_num = struct.unpack_from('<f', d, fo)[0]
                    else:
                        val_num = struct.unpack_from('<d', d, fo)[0]
                    val = str(val_num)
                else:
                    # int
                    val_num = self._read_uint_width(fo, w)
                    val = str(val_num)
            else:
                idx = self._read_uint_width(fo, w)
                group_pool = self._group_pools[group_index]
                if group_pool and i < len(group_pool) and idx < len(group_pool[i]):
                    val = group_pool[i][idx]
                else:
                    val = ''

            fname = self._field_names[i] if i < len(self._field_names) else f'field_{i}'
            fields[fname] = val

        return GeoInfo(fields=fields, field_names=self._field_names,
                       float_indices=self._float_field_indices)

    # ── bytes-based IPv6 helpers ──────────────────────────────────────

    @staticmethod
    def _bit_at_v6(ip_bytes, depth):
        return (ip_bytes[depth >> 3] >> (7 - (depth & 7))) & 1

    def _trie_walk_v6_bytes(self, ip_bytes):
        shift = 128 - self._v6_jump_bits
        hi = int.from_bytes(ip_bytes[:8], 'big')
        idx_jump = (hi >> (64 - self._v6_jump_bits)) & ((1 << self._v6_jump_bits) - 1) if self._v6_jump_bits <= 64 \
            else (int.from_bytes(ip_bytes, 'big') >> (128 - self._v6_jump_bits)) & ((1 << self._v6_jump_bits) - 1)
        ptr = self._read_u32(self._off_v6_jump + idx_jump * 4)
        if ptr == 0:
            return 0
        if ptr & SENTINEL:
            return ptr & SENTINEL_MASK_31
        idx = ptr
        depth = self._v6_jump_bits
        while depth < 128:
            bit = self._bit_at_v6(ip_bytes, depth)
            child = self._get_v6_child(idx, bit)
            if child == 0:
                return 0
            if child & SENTINEL:
                return child & SENTINEL_MASK_31
            idx = child
            depth += 1
        return 0

    # ── find / lookup ────────────────────────────────────────────────

    def find(self, ip_str):
        if not ip_str:
            return None
        parsed = _fast_parse_ip(ip_str)
        if parsed is None:
            return None
        v4, v6 = parsed
        if v4 is not None:
            return self.find_uint(v4)
        return self.find_v6_bytes(v6)

    def find_uint(self, ip_int):
        if not self._has_v4:
            return None
        row_id = self._trie_walk_v4(ip_int)
        if row_id == 0:
            return None
        return self._resolve_row_id(row_id, self._group_index)

    def find_v6_bytes(self, ip_bytes):
        """IPv6 lookup using 16-byte packed representation (zero BigInteger alloc)."""
        if not self._has_v6:
            return None
        row_id = self._trie_walk_v6_bytes(ip_bytes)
        if row_id == 0:
            return None
        return self._resolve_row_id(row_id, self._group_index)

    def find_v6_uint(self, ip_int):
        if not self._has_v6:
            return None
        row_id = self._trie_walk_v6(ip_int)
        if row_id == 0:
            return None
        return self._resolve_row_id(row_id, self._group_index)

    # ── field projection (only resolve requested fields) ─────────────

    def _resolve_geo_fields(self, entry_id, group_index, field_indices):
        if group_index < 0 or group_index >= len(self._group_field_counts):
            return {}
        if entry_id < 0 or entry_id >= self._group_entry_counts[group_index]:
            return {}
        self._ensure_pools_loaded()
        field_count = self._group_field_counts[group_index]
        if field_count <= 0:
            return {}
        group_entry_start = self._off_geo_entries + self._group_entry_offsets[group_index]
        stride = self._group_strides[group_index]
        entry_offset = group_entry_start + entry_id * stride
        d = self._data
        widths = self._group_field_widths[group_index]
        base_offsets = self._group_field_offsets[group_index]
        natives = self._group_field_native[group_index]
        nat_types = self._group_field_native_type[group_index]
        fields = {}
        for i in field_indices:
            if i < 0 or i >= field_count:
                continue
            w = widths[i]
            fo = entry_offset + base_offsets[i]
            is_native = natives and i < len(natives) and natives[i]
            if is_native:
                t = nat_types[i] if nat_types and i < len(nat_types) else 0
                if t == 1:
                    val = str(struct.unpack_from('<f' if w == 4 else '<d', d, fo)[0])
                else:
                    val_num = self._read_uint_width(fo, w)
                    val = str(val_num)
            else:
                idx = self._read_uint_width(fo, w)
                group_pool = self._group_pools[group_index]
                if group_pool and i < len(group_pool) and idx < len(group_pool[i]):
                    val = group_pool[i][idx]
                else:
                    val = ''
            fname = self._field_names[i] if i < len(self._field_names) else f'field_{i}'
            fields[fname] = val
        return fields

    def find_fields(self, ip_str, field_names=None):
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
        geo_id, asn_id, usage_type_id = self._read_ip_row(row_id)
        mask = self._group_dim_masks[self._group_index] if self._group_index < len(self._group_dim_masks) else 0
        entry_id = asn_id if (mask & 0x02) else (usage_type_id if (mask & 0x04) else geo_id)
        if entry_id == 0:
            return None
        name_to_idx = {n: i for i, n in enumerate(self._field_names)}
        indices = [name_to_idx.get(n, -1) for n in field_names if n in name_to_idx]
        if not indices:
            return None
        fields = self._resolve_geo_fields(entry_id, self._group_index, indices)
        return GeoInfo(fields=fields, field_names=self._field_names,
                       float_indices=self._float_field_indices)

    # ── lookup row id / ids ──────────────────────────────────────────

    def lookup_row_id(self, ip_str):
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
        if not self._has_v4:
            return 0
        return self._trie_walk_v4(ip_int)

    def lookup_row_id_v6(self, ip_int):
        if not self._has_v6:
            return 0
        return self._trie_walk_v6(ip_int)

    def lookup_row_id_v6_bytes(self, ip_bytes):
        if not self._has_v6:
            return 0
        return self._trie_walk_v6_bytes(ip_bytes)

    def lookup_ids(self, row_id):
        if row_id <= 0 or row_id >= self._row_count:
            return None
        return self._read_ip_row(row_id)

    def find_str(self, ip_str):
        info = self.find(ip_str)
        if info is None:
            return ''
        return info.to_pipe()

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
        if len(self._data) < 20:
            return False
        stored = struct.unpack_from('<I', self._data, 16)[0]
        original = self._data[16:20]
        mutable_data = bytearray(self._data)
        mutable_data[16:20] = b'\x00\x00\x00\x00'
        computed = zlib.crc32(mutable_data) & 0xFFFFFFFF
        return stored == computed
