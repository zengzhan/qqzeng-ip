"""
QZDB V20 (QZ20) IP Geolocation Database Reader for Python.

Format: PATRICIA Trie + IPRow indirect layer + multi-GeoEntry groups.
Supports all 4 version groups (std/ult/asn/max) with dynamic metadata.

API (matching V18 pattern):
    searcher = QzdbSearcher(db_path)
    info = searcher.find('1.2.3.4')         # -> GeoInfo
    pipe = searcher.find_str('1.2.3.4')      # -> '|' delimited string
    fields = searcher.field_names            # -> list of field names
    version = searcher.version               # -> version name string
"""

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

    def to_dict(self):
        return {fname: self._fields.get(fname, '') for fname in self._field_names}

    def to_pipe(self):
        parts = []
        for fname in self._field_names:
            val = self._fields.get(fname, '')
            if fname in self._float_indices and val:
                try:
                    val = f'{float(val):.6f}'
                except (ValueError, TypeError):
                    pass
            parts.append(str(val))
        return '|'.join(parts)


class QzdbSearcher:
    _instance = None

    @classmethod
    def get_instance(cls, db_path=None):
        if cls._instance is None:
            cls._instance = cls(db_path)
        elif db_path is not None:
            cls._instance.load(db_path)
        return cls._instance

    def __init__(self, db_path=None, version='', group_index=0):
        self._data = b''
        self._version = version
        self._group_index = group_index
        self._field_names = []
        self._float_field_indices = set()

        # Header fields
        self._flags = 0
        self._has_v4 = False
        self._has_v6 = False
        self._version_mask = 0
        self._v4_jump_bits = 16
        self._v6_jump_bits = 16
        self._pool_count = 0
        self._pool_idx_size = 2
        self._geo_count = 0
        self._row_count = 0
        self._v4_rec_count = 0
        self._v6_rec_count = 0
        self._header_size = 192
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

        # GeoEntry group metadata
        self._group_field_counts = []
        self._group_entry_counts = []
        self._group_dim_masks = []
        self._group_entry_offsets = []  # relative to _off_geo_entries

        # Pools cache: [group_idx][dim_idx][str_idx] -> str
        self._group_pools = None
        self._pools_loaded = False
        self._pools_lock = threading.Lock()

        if db_path is not None:
            self.load(db_path, version)

    def load(self, db_path, version=''):
        with open(db_path, 'rb') as f:
            self._data = f.read()
        self._parse_header()
        if version:
            self._version = version

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

    def _parse_header(self):
        d = self._data
        if len(d) < 4 or d[:4] != b'QZ20':
            raise ValueError('Invalid magic, expected QZ20')
        if len(d) < 192:
            raise ValueError('File too small for V20 header')

        fmt_ver = d[4]
        if fmt_ver not in (2, 3, 4):
            raise ValueError(f'Unsupported V20 format version: {fmt_ver}')

        self._version_mask = self._read_u16(6)
        self._flags = self._read_u16(8)
        self._has_v4 = bool(self._flags & 1)
        self._has_v6 = bool(self._flags & 2)

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

        # Read offsets
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

        # Bounds validation: raise on corrupt files instead of OOB reads.
        dlen = len(d)
        if self._off_v4_jump + 65536 * 4 > dlen:
            raise ValueError('Section v4_jump out of bounds')
        if self._off_v4_nodes + self._v4_node_count * 8 > dlen:
            raise ValueError('Section v4_nodes out of bounds')
        if self._off_v6_jump + (1 << self._v6_jump_bits) * 4 > dlen:
            raise ValueError('Section v6_jump out of bounds')
        if self._off_v6_nodes + self._v6_node_count * 8 > dlen:
            raise ValueError('Section v6_nodes out of bounds')
        if self._off_ip_row + self._row_count * self._ip_row_size > dlen:
            raise ValueError('Section ip_row out of bounds')
        if self._off_geo_entries + 1 > dlen:
            raise ValueError('Section geo_entries out of bounds')
        if self._off_pools > dlen:
            raise ValueError('Section pools out of bounds')
        if self._off_meta > dlen:
            raise ValueError('Section meta out of bounds')

        # Read GeoEntryOffsets[4] (uint48 LE, relative to OffsetGeoEntries)
        self._group_entry_offsets = []
        for i in range(4):
            off = 168 + i * 6
            val = self._read_u48(off)
            self._group_entry_offsets.append(val)

        # Parse GroupMetadataTable (at OffsetGeoEntries)
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
            if fmt_ver >= 4:
                self._group_entry_counts[gi] = self._read_u32(gm_off)
                gm_off += 4
            else:
                self._group_entry_counts[gi] = self._read_u16(gm_off)
                gm_off += 2
            if fmt_ver >= 3:
                self._group_dim_masks[gi] = self._read_u16(gm_off)
                gm_off += 2
            else:
                self._group_dim_masks[gi] = 0x01 if gi != 2 else 0x02

        if actual_groups == 0:
            self._group_field_counts = [self._pool_count]
            self._group_entry_counts = [self._geo_count]
            self._group_entry_offsets = [0, 0, 0, 0]
            self._group_dim_masks = [0x01]

        # Resolve field names from metadata
        self._resolve_field_names()

    def _resolve_field_names(self):
        """Read field names from Metadata section (type=2)."""
        d = self._data
        off_meta = self._off_meta
        if (self._flags & 4) and off_meta > 0 and off_meta + 4 <= len(d):
            field_names = None
            version_name = None
            pos = off_meta
            while pos + 4 <= len(d):
                t = d[pos]
                length = self._read_u16(pos + 2)
                if t == 0 or length == 0:
                    break
                val = d[pos + 4:pos + 4 + length].decode('utf-8')
                if t == 1:
                    version_name = val
                elif t == 2:
                    field_names = val.split('|')
                pos += 4 + length

            # Validate: field_names must match primary group's field count
            if field_names and len(field_names) == self._group_field_counts[0]:
                self._field_names = field_names
                self._float_field_indices = {
                    i for i, n in enumerate(field_names)
                    if n in FLOAT_FIELDS
                }
                if version_name:
                    self._version = version_name
                return

        # Fallback: if no metadata, try to infer from pool count (legacy)
        pc_map = {5: 'std', 7: 'asn', 11: 'ult', 25: 'max'}
        ver = pc_map.get(self._pool_count, '')
        if ver:
            from importlib import import_module
            try:
                # Try V18 module's field definitions
                ref = import_module('qzdb')
                fields = ref.VERSION_FIELDS.get(ver, [])
                if fields:
                    self._field_names = fields
                    self._float_field_indices = {
                        i for i, n in enumerate(fields)
                        if n in FLOAT_FIELDS
                    }
                    if not self._version:
                        self._version = ver
                    return
            except ImportError:
                pass

        # Last resort: use placeholder names
        self._field_names = [f'field_{i}' for i in range(self._group_field_counts[0])]
        self._float_field_indices = set()

    # ── String Pool Loading ──

    def _ensure_pools_loaded(self):
        if self._pools_loaded:
            return
        with self._pools_lock:
            if self._pools_loaded:
                return

            group_count = len(self._group_field_counts)
            self._group_pools = [None] * group_count

            if self._off_pools <= 0:
                self._pools_loaded = True
                return

            pool_cursor = self._off_pools
            pool_end = self._off_meta if self._off_meta > 0 else len(self._data)
            d = self._data

            for g in range(group_count):
                field_count = self._group_field_counts[g]
                group_pool_list = []
                for f in range(field_count):
                    if pool_cursor + 4 > pool_end:
                        group_pool_list.append([])
                        continue
                    count = self._read_u32(pool_cursor)
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

    # ── Trie Walk ──

    def _trie_walk_v4(self, ip_int):
        d = self._data
        hi16 = (ip_int >> 16) & 0xFFFF
        ptr = struct.unpack_from('<I', d, self._off_v4_jump + hi16 * 4)[0]

        if ptr == 0:
            return 0
        if ptr & SENTINEL:
            return ptr & 0x7FFFFFFF

        idx = ptr
        suffix = (ip_int & 0xFFFF) << 16
        nodes_off = self._off_v4_nodes
        steps = 0

        while True:
            steps += 1
            if steps > 32:
                return 0
            if idx >= self._v4_node_count:
                return 0
            bit = (suffix >> 31) & 1
            # nodes[idx] = { left: uint32, right: uint32 }
            child_off = nodes_off + idx * 8 + bit * 4
            child = struct.unpack_from('<I', d, child_off)[0]

            if child == 0:
                return 0
            if child & SENTINEL:
                return child & 0x7FFFFFFF

            idx = child
            suffix <<= 1

    def _trie_walk_v6(self, ip_int):
        d = self._data
        shift = 128 - self._v6_jump_bits
        idx_jump = (ip_int >> shift) & ((1 << self._v6_jump_bits) - 1)
        ptr = struct.unpack_from('<I', d, self._off_v6_jump + idx_jump * 4)[0]

        if ptr == 0:
            return 0
        if ptr & SENTINEL:
            return ptr & 0x7FFFFFFF

        idx = ptr
        depth = self._v6_jump_bits
        nodes_off = self._off_v6_nodes

        while depth < 128:
            if idx >= self._v6_node_count:
                return 0
            bit = (ip_int >> (127 - depth)) & 1
            child_off = nodes_off + idx * 8 + bit * 4
            child = struct.unpack_from('<I', d, child_off)[0]

            if child == 0:
                return 0
            if child & SENTINEL:
                return child & 0x7FFFFFFF

            idx = child
            depth += 1

        return 0

    # ── IPRow Resolution ──

    def _read_ip_row(self, row_id):
        if row_id <= 0 or row_id >= self._row_count:
            return 0, 0, 0
        off = self._off_ip_row + row_id * self._ip_row_size
        d = self._data
        geo_id = d[off] | (d[off + 1] << 8) | (d[off + 2] << 16)
        asn_id = d[off + 3] | (d[off + 4] << 8) | (d[off + 5] << 16)

        usage_type_id = 0
        if self._ip_row_size >= 9:
            usage_type_id = d[off + 6] | (d[off + 7] << 8) | (d[off + 8] << 16)

        return geo_id, asn_id, usage_type_id

    # ── Geo Resolution ──

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

        # Calculate GeoEntry data offset
        group_entry_start = self._off_geo_entries + self._group_entry_offsets[group_index]
        entry_offset = group_entry_start + entry_id * field_count * self._pool_idx_size
        d = self._data

        # Read pool indices
        pool_idxs = []
        for i in range(field_count):
            if self._pool_idx_size == 2:
                idx = struct.unpack_from('<H', d, entry_offset)[0]
            elif self._pool_idx_size == 3:
                idx = d[entry_offset] | (d[entry_offset + 1] << 8) | (d[entry_offset + 2] << 16)
            else:
                idx = struct.unpack_from('<I', d, entry_offset)[0]
            pool_idxs.append(idx)
            entry_offset += self._pool_idx_size

        # Resolve pool values
        group_pool = self._group_pools[group_index] if self._group_pools else None
        if group_pool is None:
            return None

        # Use primary group field names if available, else this group's field names
        field_names = self._field_names

        fields = {}
        for i in range(field_count):
            idx = pool_idxs[i]
            if group_pool and i < len(group_pool) and idx < len(group_pool[i]):
                val = group_pool[i][idx]
            else:
                val = ''
            fname = field_names[i] if i < len(field_names) else f'field_{i}'
            fields[fname] = val

        return GeoInfo(fields=fields, field_names=field_names,
                       float_indices=self._float_field_indices)

    # ── Public API ──

    def find(self, ip_str):
        """Lookup IP and return GeoInfo. Accepts both IPv4 and IPv6 strings."""
        if not ip_str:
            return None
        try:
            ip_obj = ipaddress.ip_address(ip_str)
        except ValueError:
            return None

        if isinstance(ip_obj, ipaddress.IPv4Address):
            return self.find_uint(int(ip_obj))
        else:
            # Handle IPv4-mapped IPv6 (::ffff:x.x.x.x)
            ip_int = int(ip_obj)
            if ip_obj.ipv4_mapped:
                return self.find_uint(int(ip_obj.ipv4_mapped))
            return self.find_v6_uint(ip_int)

    def find_uint(self, ip_int, group_index=None):
        """Lookup IPv4 uint32, return GeoInfo."""
        if not self._has_v4:
            return None
        if group_index is None:
            group_index = self._group_index
        row_id = self._trie_walk_v4(ip_int)
        if row_id == 0:
            return None
        return self._resolve_row_id(row_id, group_index)

    def find_v6_uint(self, ip_int, group_index=None):
        """Lookup IPv6 uint128, return GeoInfo."""
        if not self._has_v6:
            return None
        if group_index is None:
            group_index = self._group_index
        row_id = self._trie_walk_v6(ip_int)
        if row_id == 0:
            return None
        return self._resolve_row_id(row_id, group_index)

    def find_str(self, ip_str):
        """Lookup IP and return pipe-delimited string."""
        info = self.find(ip_str)
        if info is None:
            return ''
        return info.to_pipe()

    def verify_crc(self) -> bool:
        """Verify CRC32 checksum (bytes 16-19 store CRC, zeroed during computation)."""
        d = self._data
        if len(d) < 20:
            return False
        stored = struct.unpack_from('<I', d, 16)[0]
        # Zero out CRC bytes for computation
        data_copy = bytearray(d)
        data_copy[16:20] = b'\x00\x00\x00\x00'
        computed = zlib.crc32(bytes(data_copy)) & 0xFFFFFFFF
        return stored == computed

    @property
    def field_names(self):
        return self._field_names

    @property
    def version(self):
        return self._version

    @property
    def pool_count(self):
        return self._pool_count

    def set_group(self, group_index):
        """Set default version group index (0=std, 1=ult, 2=asn, 3=max)."""
        self._group_index = group_index


# ── CLI Test ──
if __name__ == '__main__':
    import sys
    if len(sys.argv) < 2:
        print('Usage: python qzdb_v20.py <db_path> [ip_address]')
        sys.exit(1)

    db_path = sys.argv[1]
    searcher = QzdbSearcher(db_path)

    if len(sys.argv) >= 3:
        ip = sys.argv[2]
        result = searcher.find(ip)
        if result:
            print(result.to_pipe())
        else:
            print('Not found')
    else:
        # Interactive test
        test_ips = ['1.2.3.4', '8.8.8.8', '114.114.114.114', '::1', '2001:4860:4860::8888']
        for ip in test_ips:
            result = searcher.find(ip)
            if result:
                print(f'{ip}: {result.to_pipe()}')
            else:
                print(f'{ip}: Not found')
        print(f'Version: {searcher.version}')
        print(f'Fields: {searcher.field_names}')
        print(f'CRC32: {"PASS" if searcher.verify_crc() else "FAIL"}')
