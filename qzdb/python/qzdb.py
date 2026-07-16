import ipaddress
import struct
import zlib
import threading

SENTINEL = 0x80000000
FLOAT_FIELDS = frozenset(['longitude', 'latitude'])


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

        if db_path is not None:
            self.load(db_path)

    def load(self, db_path):
        with open(db_path, 'rb') as f:
            self._data = f.read()
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
            raise ValueError('File too small for QZDB header')

        magic = d[:4]
        if magic != b'QZDB':
            raise ValueError('Invalid magic, expected QZDB')

        fmt_ver = d[4]
        # QZDB format version 1-6 are all supported
        if fmt_ver not in (1, 2, 3, 4, 5, 6):
            raise ValueError(f'Unsupported format version: {fmt_ver}')

        self._flags = self._read_u16(8)
        self._has_v4 = bool(self._flags & 1)
        self._has_v6 = bool(self._flags & 2)
        self._v4_node_24 = bool(self._flags & 0x10)
        self._v6_node_24 = bool(self._flags & 0x20)

        self._v6_jump_bits = d[11]
        if self._v6_jump_bits == 0:
            self._v6_jump_bits = 16

        self._pool_count = d[12]
        self._pool_idx_size = d[13]
        self._geo_count = self._read_u16(14)
        self._row_count = self._read_u32(20)
        self._v4_rec_count = self._read_u32(24)
        self._v6_rec_count = self._read_u32(28)

        hs = self._read_u32(36)
        if hs != 192:
            raise ValueError(f'Unexpected header size: {hs}')

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
        self._geo_entry_group_count = self._read_u32(164)

        # GeoEntryOffsets[4]
        self._group_entry_offsets = []
        for i in range(4):
            self._group_entry_offsets.append(self._read_u48(168 + i * 6))

        # Parse GroupMetadataTable (at off_geo_entries)
        gm_off = self._off_geo_entries
        group_count = d[gm_off]
        gm_off += 1

        actual_groups = min(group_count, max(1, self._geo_entry_group_count))
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
        self._pools_loaded = True

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

    def _get_v4_child(self, node_idx, bit):
        if self._v4_node_24:
            node_offset = self._off_v4_nodes + node_idx * 6
            offset = node_offset if bit == 0 else node_offset + 3
            val = self._data[offset] | (self._data[offset + 1] << 8) | (self._data[offset + 2] << 16)
            if val & 0x800000:
                return (val & 0x7FFFFF) | SENTINEL
            return val
        else:
            child_off = self._off_v4_nodes + node_idx * 8 + bit * 4
            return self._read_u32(child_off)

    def _get_v6_child(self, node_idx, bit):
        if self._v6_node_24:
            node_offset = self._off_v6_nodes + node_idx * 6
            offset = node_offset if bit == 0 else node_offset + 3
            val = self._data[offset] | (self._data[offset + 1] << 8) | (self._data[offset + 2] << 16)
            if val & 0x800000:
                return (val & 0x7FFFFF) | SENTINEL
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
            return ptr & 0x7FFFFFFF

        idx = ptr
        suffix = (ip_int & 0xFFFF) << 16

        while True:
            bit = (suffix >> 31) & 1
            child = self._get_v4_child(idx, bit)

            if child == 0:
                return 0
            if child & SENTINEL:
                return child & 0x7FFFFFFF

            idx = child
            suffix <<= 1

    def _trie_walk_v6(self, ip_int):
        shift = 128 - self._v6_jump_bits
        idx_jump = (ip_int >> shift) & ((1 << self._v6_jump_bits) - 1)
        ptr = self._read_u32(self._off_v6_jump + idx_jump * 4)

        if ptr == 0:
            return 0
        if ptr & SENTINEL:
            return ptr & 0x7FFFFFFF

        idx = ptr
        depth = self._v6_jump_bits

        while depth < 128:
            bit = (ip_int >> (127 - depth)) & 1
            child = self._get_v6_child(idx, bit)

            if child == 0:
                return 0
            if child & SENTINEL:
                return child & 0x7FFFFFFF

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

    def find(self, ip_str):
        if not ip_str:
            return None
        try:
            ip_obj = ipaddress.ip_address(ip_str)
        except ValueError:
            return None

        if isinstance(ip_obj, ipaddress.IPv4Address):
            return self.find_uint(int(ip_obj))
        else:
            ip_int = int(ip_obj)
            # Check for IPv4-mapped IPv6 (::ffff:x.x.x.x)
            if ip_obj.ipv4_mapped:
                return self.find_uint(int(ip_obj.ipv4_mapped))
            return self.find_v6_uint(ip_int)

    def find_uint(self, ip_int):
        if not self._has_v4:
            return None
        row_id = self._trie_walk_v4(ip_int)
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
