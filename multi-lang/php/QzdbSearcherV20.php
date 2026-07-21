<?php
namespace Qqzeng\Ip;

class QzdbSearcherV20
{
    private static $instance = null;
    private $data;
    private $fmtVer;
    private $hasV4 = false;
    private $hasV6 = false;
    private $v6JumpBits = 16;
    private $poolCount;
    private $poolIdxSize;
    private $geoCount;
    private $rowCount;
    private $v4NodeCount;
    private $v6NodeCount;
    private $ipRowSize;
    private $geoEntryGroupCount;

    private $offV4Jump;
    private $offV4Nodes;
    private $offV6Jump;
    private $offV6Nodes;
    private $offIPRow;
    private $offGeoEntries;
    private $offPools;
    private $offMeta;

    private $groupFieldCounts = [];
    private $groupEntryCounts = [];
    private $groupDimMasks = [];
    private $groupEntryOffsets = [];

    private $groupPools = [];
    private $poolsLoaded = false;
    private $groupIndex = 0;

    private $fieldNames = [];
    private $floatIndices = [];
    private $versionName = '';
    private $flags = 0;

    const FLOAT_FIELDS = ['longitude', 'latitude'];

    public static function getInstance($dbPath = null, $groupIndex = 0)
    {
        if (self::$instance === null) {
            self::$instance = new self($dbPath, $groupIndex);
        }
        return self::$instance;
    }

    public function __construct($dbPath = null, $groupIndex = 0)
    {
        if ($dbPath !== null) {
            $this->load($dbPath, $groupIndex);
        }
    }

    public function load($dbPath, $groupIndex = 0)
    {
        $this->groupIndex = $groupIndex;
        $this->data = file_get_contents($dbPath);
        $this->parseHeader();
    }

    public function setGroup($groupIndex)
    {
        $this->groupIndex = $groupIndex;
    }

    private function readU16($off)
    {
        return unpack('v', $this->data, $off)[1];
    }

    private function readU32($off)
    {
        return unpack('V', $this->data, $off)[1];
    }

    private function readU64($off)
    {
        return unpack('P', $this->data, $off)[1];
    }

    private function readU24($off)
    {
        $d = $this->data;
        return ord($d[$off]) | (ord($d[$off + 1]) << 8) | (ord($d[$off + 2]) << 16);
    }

    private function readU48($off)
    {
        $d = $this->data;
        return ord($d[$off]) | (ord($d[$off + 1]) << 8) | (ord($d[$off + 2]) << 16)
            | (ord($d[$off + 3]) << 24) | (ord($d[$off + 4]) << 32) | (ord($d[$off + 5]) << 40);
    }

    private function parseHeader()
    {
        $d = $this->data;
        if (substr($d, 0, 4) !== 'QZ20') {
            throw new \Exception('Invalid V20 magic, expected QZ20');
        }

        $this->fmtVer = ord($d[4]);
        if ($this->fmtVer !== 2 && $this->fmtVer !== 3 && $this->fmtVer !== 4) {
            throw new \Exception('Unsupported V20 format version: ' . $this->fmtVer);
        }

        $this->flags = $this->readU16(8);
        $this->hasV4 = (bool)($this->flags & 1);
        $this->hasV6 = (bool)($this->flags & 2);

        $this->v6JumpBits = ord($d[11]) ?: 16;
        $this->poolCount = ord($d[12]);
        $this->poolIdxSize = ord($d[13]);
        $this->geoCount = $this->readU16(14);
        $this->rowCount = $this->readU32(20);

        $hs = $this->readU32(36);
        if ($hs !== 192) throw new \Exception('Unexpected header size: ' . $hs);

        $this->offV4Jump = $this->readU64(64);
        $this->offV4Nodes = $this->readU64(72);
        $this->offV6Jump = $this->readU64(80);
        $this->offV6Nodes = $this->readU64(88);
        $this->offIPRow = $this->readU64(96);
        $this->offGeoEntries = $this->readU64(104);
        $this->offPools = $this->readU64(136);
        $this->offMeta = $this->readU64(144);

        $this->v4NodeCount = $this->readU32(152);
        $this->v6NodeCount = $this->readU32(156);
        $this->ipRowSize = $this->readU32(160);
        $this->geoEntryGroupCount = $this->readU32(164);

        $d = $this->data;
        $len = strlen($d);
        
        if ($this->offV4Jump + 65536 * 4 > $len) {
            throw new \Exception('V4 jump table out of bounds');
        }
        if ($this->offV4Nodes + $this->v4NodeCount * 8 > $len) {
            throw new \Exception('V4 nodes table out of bounds');
        }
        if ($this->offV6Jump + 65536 * 4 > $len) {
            throw new \Exception('V6 jump table out of bounds');
        }
        if ($this->offV6Nodes + $this->v6NodeCount * 8 > $len) {
            throw new \Exception('V6 nodes table out of bounds');
        }
        if ($this->offIPRow + $this->rowCount * $this->ipRowSize > $len) {
            throw new \Exception('IP row table out of bounds');
        }
        if ($this->offGeoEntries + $this->geoEntryGroupCount * 20 > $len) {
            throw new \Exception('Geo entries table out of bounds');
        }
        if ($this->offPools > $len) {
            throw new \Exception('Pools table out of bounds');
        }
        if ($this->offMeta > $len) {
            throw new \Exception('Meta table out of bounds');
        }

        // GeoEntryOffsets[4]
        $this->groupEntryOffsets = [];
        for ($i = 0; $i < 4; $i++) {
            $this->groupEntryOffsets[] = $this->readU48(168 + $i * 6);
        }

        // Parse GroupMetadataTable at offGeoEntries
        $gmOff = $this->offGeoEntries;
        $groupCount = ord($d[$gmOff++]);

        $actualGroups = max(1, $groupCount);
        if ($this->geoEntryGroupCount > 0 && $this->geoEntryGroupCount < $actualGroups) {
            $actualGroups = $this->geoEntryGroupCount;
        }
        if ($actualGroups > 4) $actualGroups = 4;

        $this->groupFieldCounts = [];
        $this->groupEntryCounts = [];
        $this->groupDimMasks = [];

        for ($gi = 0; $gi < $actualGroups; $gi++) {
            $this->groupFieldCounts[] = ord($d[$gmOff++]);
            if ($this->fmtVer >= 4) {
                $this->groupEntryCounts[] = $this->readU32($gmOff);
                $gmOff += 4;
            } else {
                $this->groupEntryCounts[] = $this->readU16($gmOff);
                $gmOff += 2;
            }
            if ($this->fmtVer >= 3) {
                $this->groupDimMasks[] = $this->readU16($gmOff);
                $gmOff += 2;
            } else {
                $this->groupDimMasks[] = ($gi !== 2) ? 0x01 : 0x02;
            }
        }

        // Fallback for empty group
        if ($actualGroups === 1 && $groupCount === 0) {
            $this->groupFieldCounts[0] = $this->poolCount;
            $this->groupEntryCounts[0] = $this->geoCount;
            $this->groupDimMasks[0] = 0x01;
            $this->groupEntryOffsets[0] = 0;
        }

        $this->resolveFieldNames();
    }

    private function resolveFieldNames()
    {
        if (!($this->flags & 4) || !$this->offMeta) return;

        $d = $this->data;
        $pos = $this->offMeta;
        $end = strlen($d);
        $fieldNames = null;

        while ($pos + 4 <= $end) {
            $type = ord($d[$pos]);
            $len = $this->readU16($pos + 2);
            if ($type === 0 || $len === 0) break;
            if ($pos + 4 + $len > $end) break;
            $val = substr($d, $pos + 4, $len);
            if ($type === 1) $this->versionName = $val;
            else if ($type === 2) $fieldNames = explode('|', $val);
            $pos += 4 + $len;
        }

        if ($fieldNames !== null && count($fieldNames) === $this->groupFieldCounts[0]) {
            $this->fieldNames = $fieldNames;
            $this->floatIndices = [];
            foreach ($fieldNames as $i => $n) {
                if (in_array($n, self::FLOAT_FIELDS, true)) {
                    $this->floatIndices[] = $i;
                }
            }
        }
    }

    private function ensurePoolsLoaded()
    {
        if ($this->poolsLoaded) return;
        $this->poolsLoaded = true;

        $groupCount = count($this->groupFieldCounts);
        $this->groupPools = [];

        if (!$this->offPools) return;

        $poolEnd = $this->offMeta ?: strlen($this->data);
        $cursor = $this->offPools;
        $d = $this->data;

        for ($g = 0; $g < $groupCount; $g++) {
            $fieldCount = $this->groupFieldCounts[$g];
            $groupPools = [];

            for ($f = 0; $f < $fieldCount; $f++) {
                if ($cursor + 4 > $poolEnd) { $groupPools[] = []; continue; }
                $count = $this->readU32($cursor);
                $cursor += 4;
                if ($count === 0) { $groupPools[] = []; continue; }

                $offsets = [];
                for ($j = 0; $j <= $count; $j++) {
                    $offsets[] = $this->readU32($cursor);
                    $cursor += 4;
                }

                $strings = [];
                for ($si = 0; $si < $count; $si++) {
                    $start = $offsets[$si];
                    $end = $offsets[$si + 1];
                    $strings[] = $end > $start ? substr($d, $cursor + $start, $end - $start) : '';
                }
                $cursor += $offsets[$count];
                $groupPools[] = $strings;
            }
            $this->groupPools[] = $groupPools;
        }
    }

    // ── PATRICIA Trie Walk ──

    private function trieWalkV4($ipInt)
    {
        $d = $this->data;
        $hi16 = ($ipInt >> 16) & 0xFFFF;
        $ptr = $this->readU32($this->offV4Jump + $hi16 * 4);

        if ($ptr === 0) return 0;
        if ($ptr & 0x80000000) return $ptr & 0x7FFFFFFF;

        $idx = $ptr;
        $suffix = ($ipInt & 0xFFFF) << 16;
        $nodesOff = $this->offV4Nodes;
        $steps = 0;

        while (true) {
            $bit = ($suffix >> 31) & 1;
            $childOff = $nodesOff + $idx * 8 + $bit * 4;
            $child = $this->readU32($childOff);
            if ($child === 0) return 0;
            if ($child & 0x80000000) return $child & 0x7FFFFFFF;
            $idx = $child;
            $suffix <<= 1;
            if (++$steps > 32) return 0;
        }
    }

    private function trieWalkV6($ipHi, $ipLo)
    {
        $d = $this->data;
        $shift = 128 - $this->v6JumpBits;

        if ($shift >= 64) {
            $idxJump = $ipHi >> ($shift - 64);
        } else {
            $idxJump = ($ipHi << (64 - $shift)) | ($ipLo >> $shift);
        }
        $mask = (1 << $this->v6JumpBits) - 1;
        $idxJump &= $mask;

        $ptr = $this->readU32($this->offV6Jump + $idxJump * 4);
        if ($ptr === 0) return 0;
        if ($ptr & 0x80000000) return $ptr & 0x7FFFFFFF;

        $idx = $ptr;
        $depth = $this->v6JumpBits;
        $nodesOff = $this->offV6Nodes;

        while ($depth < 128) {
            $bitPos = 127 - $depth;
            $bit = $bitPos >= 64
                ? ($ipHi >> ($bitPos - 64)) & 1
                : ($ipLo >> $bitPos) & 1;

            $childOff = $nodesOff + $idx * 8 + $bit * 4;
            $child = $this->readU32($childOff);
            if ($child === 0) return 0;
            if ($child & 0x80000000) return $child & 0x7FFFFFFF;
            $idx = $child;
            $depth++;
        }
        return 0;
    }

    // ── IPRow Reading ──

    private function readIPRow($rowId)
    {
        if ($rowId <= 0 || $rowId >= $this->rowCount) return [0, 0, 0];
        $off = $this->offIPRow + $rowId * $this->ipRowSize;
        $d = $this->data;
        $geo = ord($d[$off]) | (ord($d[$off + 1]) << 8) | (ord($d[$off + 2]) << 16);
        $asn = ord($d[$off + 3]) | (ord($d[$off + 4]) << 8) | (ord($d[$off + 5]) << 16);
        $usage = $this->ipRowSize >= 9
            ? (ord($d[$off + 6]) | (ord($d[$off + 7]) << 8) | (ord($d[$off + 8]) << 16))
            : 0;
        return [$geo, $asn, $usage];
    }

    // ── GeoEntry Resolution ──

    private function resolveRowID($rowId, $groupIndex)
    {
        [$geoId, $asnId, $usageId] = $this->readIPRow($rowId);
        $mask = isset($this->groupDimMasks[$groupIndex]) ? $this->groupDimMasks[$groupIndex] : 0;
        $entryId = ($mask & 0x02) ? $asnId : (($mask & 0x04) ? $usageId : $geoId);
        if ($entryId === 0) return null;
        return $this->resolveGeo($entryId, $groupIndex);
    }

    private function resolveGeo($entryId, $groupIndex)
    {
        if (!isset($this->groupFieldCounts[$groupIndex])) return null;
        if ($entryId < 0) return null;
        if ($entryId >= $this->groupEntryCounts[$groupIndex]) return null;

        $this->ensurePoolsLoaded();

        $fieldCount = $this->groupFieldCounts[$groupIndex];
        if ($fieldCount <= 0) return null;

        $groupEntryStart = $this->offGeoEntries + $this->groupEntryOffsets[$groupIndex];
        $entryOff = $groupEntryStart + $entryId * $fieldCount * $this->poolIdxSize;
        $d = $this->data;

        // Read pool indices
        $poolIdxs = [];
        for ($i = 0; $i < $fieldCount; $i++) {
            if ($this->poolIdxSize === 2) {
                $poolIdxs[] = $this->readU16($entryOff);
            } elseif ($this->poolIdxSize === 3) {
                $poolIdxs[] = $this->readU24($entryOff);
            } else {
                $poolIdxs[] = $this->readU32($entryOff);
            }
            $entryOff += $this->poolIdxSize;
        }

        if (!isset($this->groupPools[$groupIndex])) return null;
        $groupPool = $this->groupPools[$groupIndex];

        $fnames = $this->fieldNames;
        if (empty($fnames)) {
            $fnames = [];
            for ($i = 0; $i < $fieldCount; $i++) $fnames[] = "field_{$i}";
        }

        $vals = [];
        for ($i = 0; $i < $fieldCount; $i++) {
            $idx = $poolIdxs[$i];
            $vals[] = (isset($groupPool[$i][$idx])) ? $groupPool[$i][$idx] : '';
        }

        return ['values' => $vals, 'fieldNames' => $fnames];
    }

    // ── Public API ──

    public function find($ipStr)
    {
        if (!$ipStr) return null;

        $groupIdx = $this->groupIndex;
        $bangPos = strpos($ipStr, '!');
        if ($bangPos !== false) {
            $gi = (int)substr($ipStr, $bangPos + 1);
            $groupIdx = $gi;
            $ipStr = substr($ipStr, 0, $bangPos);
        }

        if (strpos($ipStr, ':') !== false) {
            // IPv6
            $packed = inet_pton($ipStr);
            if ($packed === false) return null;
            if (substr($packed, 0, 12) === "\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\xff\xff") {
                $ipInt = unpack('N', substr($packed, 12))[1];
                return $this->lookupV4($ipInt, $groupIdx);
            }
            $hi = unpack('J', substr($packed, 0, 8))[1];
            $lo = unpack('J', substr($packed, 8, 8))[1];
            return $this->lookupV6($hi, $lo, $groupIdx);
        }

        $ipInt = self::fastParseIp($ipStr);
        if ($ipInt === null) return null;
        return $this->lookupV4($ipInt, $groupIdx);
    }

    public function findStr($ipStr)
    {
        $info = $this->find($ipStr);
        if ($info === null) return '';

        $fnames = $info['fieldNames'];
        $vals = $info['values'];
        $floatSet = array_flip($this->floatIndices);
        $parts = [];
        foreach ($fnames as $i => $name) {
            $val = isset($vals[$i]) ? $vals[$i] : '';
            if (isset($floatSet[$i]) && $val !== '') {
                $parts[] = number_format((float)$val, 6, '.', '');
            } else {
                $parts[] = $val;
            }
        }
        return implode('|', $parts);
    }

    private function lookupV4($ipInt, $groupIndex)
    {
        if (!$this->hasV4) return null;
        $rowId = $this->trieWalkV4($ipInt);
        if ($rowId === 0) return null;
        return $this->resolveRowID($rowId, $groupIndex);
    }

    private function lookupV6($ipHi, $ipLo, $groupIndex)
    {
        if (!$this->hasV6) return null;
        $rowId = $this->trieWalkV6($ipHi, $ipLo);
        if ($rowId === 0) return null;
        return $this->resolveRowID($rowId, $groupIndex);
    }

    public function findUint($ipInt)
    {
        return $this->lookupV4($ipInt, $this->groupIndex);
    }

    // ── Utility ──

    private static function fastParseIp($ip)
    {
        $result = 0;
        $val = 0;
        $dots = 0;
        $len = strlen($ip);
        for ($i = 0; $i < $len; $i++) {
            $c = ord($ip[$i]);
            if ($c >= 48 && $c <= 57) {
                $val = $val * 10 + ($c - 48);
                if ($val > 255) return null;
            } elseif ($c === 46) {
                if ($i === 0 || $ip[$i - 1] === '.') return null;
                $result = ($result << 8) | $val;
                $val = 0;
                $dots++;
            } else {
                return null;
            }
        }
        if ($dots !== 3) return null;
        return ($result << 8) | $val;
    }

    public function getFieldNames()
    {
        return $this->fieldNames;
    }

    public function verifyCrc()
    {
        if (strlen($this->data) < 20) return false;
        $stored = unpack('V', $this->data, 16)[1];
        $this->data[16] = "\x00";
        $this->data[17] = "\x00";
        $this->data[18] = "\x00";
        $this->data[19] = "\x00";
        $computed = hexdec(hash('crc32b', $this->data));
        $this->data[16] = chr($stored & 0xFF);
        $this->data[17] = chr(($stored >> 8) & 0xFF);
        $this->data[18] = chr(($stored >> 16) & 0xFF);
        $this->data[19] = chr(($stored >> 24) & 0xFF);
        return $stored === $computed;
    }
}
