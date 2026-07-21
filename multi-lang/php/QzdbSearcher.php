<?php
namespace Qqzeng\Ip;

class GeoInfo implements \ArrayAccess
{
    private $fields;
    private $fieldNames;
    private $floatIndices;

    public function __construct(array $fields = [], array $fieldNames = [], array $floatIndices = [])
    {
        $this->fields = $fields;
        $this->fieldNames = $fieldNames;
        $this->floatIndices = array_flip($floatIndices);
    }

    public function __get($name)
    {
        return $this->fields[$name] ?? '';
    }

    public function get($name)
    {
        return $this->fields[$name] ?? '';
    }

    public function offsetExists($offset): bool
    {
        return isset($this->fields[$offset]);
    }

    #[\ReturnTypeWillChange]
    public function offsetGet($offset)
    {
        return $this->fields[$offset] ?? '';
    }

    public function offsetSet($offset, $value): void
    {
        $this->fields[$offset] = $value;
    }

    public function offsetUnset($offset): void
    {
        unset($this->fields[$offset]);
    }

    public function toPipe()
    {
        $parts = [];
        foreach ($this->fieldNames as $fname) {
            $val = $this->fields[$fname] ?? '';
            if (isset($this->floatIndices[$fname]) && $val !== '') {
                $val = sprintf('%.6f', (float)$val);
            }
            $parts[] = (string)$val;
        }
        return implode('|', $parts);
    }
}

class QzdbSearcher
{
    private static $instance = null;
    private $data;
    private $groupIndex = 0;
    private $fieldNames = [];
    private $floatFieldIndices = [];
    private $versionName = '';

    // Header fields
    private $flags = 0;
    private $hasV4 = false;
    private $hasV6 = false;
    private $v4Node24 = false;
    private $v6Node24 = false;
    private $v6JumpBits = 16;
    private $poolCount = 0;
    private $poolIdxSize = 2;
    private $geoCount = 0;
    private $rowCount = 0;
    private $v4RecCount = 0;
    private $v6RecCount = 0;
    private $v4NodeCount = 0;
    private $v6NodeCount = 0;
    private $ipRowSize = 6;
    private $geoEntryGroupCount = 0;

    // Offsets
    private $offV4Jump = 0;
    private $offV4Nodes = 0;
    private $offV6Jump = 0;
    private $offV6Nodes = 0;
    private $offIPRow = 0;
    private $offGeoEntries = 0;
    private $offPools = 0;
    private $offMeta = 0;
    private $offRowSchema = 0;
    private $offGroupSchema = 0;

    // Schema and layout cache
    private $groupFieldCounts = [];
    private $groupEntryCounts = [];
    private $groupDimMasks = [];
    private $groupEntryOffsets = [];

    private $groupStrides = [];
    private $groupFieldWidths = [];
    private $groupFieldOffsets = [];
    private $groupFieldNative = [];
    private $groupFieldNativeType = [];
    private $groupFieldIds = [];
    private $groupPoolSectionIds = [];

    private $groupPools = null;
    private $poolsLoaded = false;

    const SENTINEL = 0x80000000;
    const FLOAT_FIELDS = ['longitude' => true, 'latitude' => true];

    public static function getInstance($dbPath = null, $groupIndex = 0)
    {
        if (self::$instance === null) {
            self::$instance = new self($dbPath, $groupIndex);
        } elseif ($dbPath !== null) {
            self::$instance->load($dbPath);
            self::$instance->groupIndex = $groupIndex;
        }
        return self::$instance;
    }

    public function __construct($dbPath = null, $groupIndex = 0)
    {
        $this->groupIndex = $groupIndex;
        // Set locale to C for locale-independent float formatting
        setlocale(LC_NUMERIC, 'C');
        if ($dbPath !== null) {
            $this->load($dbPath);
        }
    }

    public function load($dbPath)
    {
        $this->data = file_get_contents($dbPath);
        if ($this->data === false) {
            throw new \InvalidArgumentException("Cannot read database file: " . $dbPath);
        }
        $this->parseHeader();
    }

    private function readU16($off)
    {
        return unpack('v', substr($this->data, $off, 2))[1];
    }

    private function readU32($off)
    {
        return unpack('V', substr($this->data, $off, 4))[1];
    }

    private function readU64($off)
    {
        return unpack('P', substr($this->data, $off, 8))[1];
    }

    private function readU24($off)
    {
        $d = $this->data;
        return ord($d[$off]) | (ord($d[$off + 1]) << 8) | (ord($d[$off + 2]) << 16);
    }

    private function readU48($off)
    {
        $d = $this->data;
        $low = unpack('V', substr($d, $off, 4))[1];
        $high = unpack('v', substr($d, $off + 4, 2))[1];
        return $low + ($high * 4294967296);
    }

    private function readUintWidth($off, $width)
    {
        if ($width <= 1) {
            return ord($this->data[$off]);
        } elseif ($width == 2) {
            return $this->readU16($off);
        } elseif ($width == 3) {
            return $this->readU24($off);
        } else {
            return $this->readU32($off);
        }
    }

    private function parseHeader()
    {
        $d = $this->data;
        if (strlen($d) < 192) {
            throw new \RuntimeException('File too small for QZDB header');
        }

        $magic = substr($d, 0, 4);
        if ($magic !== 'QZDB') {
            throw new \RuntimeException('Invalid magic, expected QZDB');
        }

        $fmtVer = ord($d[4]);
        if ($fmtVer < 1 || $fmtVer > 6) {
            throw new \RuntimeException("Unsupported format version: {$fmtVer}");
        }

        $this->flags = $this->readU16(8);
        $this->hasV4 = (bool)($this->flags & 1);
        $this->hasV6 = (bool)($this->flags & 2);
        $this->v4Node24 = (bool)($this->flags & 0x10);
        $this->v6Node24 = (bool)($this->flags & 0x20);

        $this->v6JumpBits = ord($d[11]);
        if ($this->v6JumpBits === 0) {
            $this->v6JumpBits = 16;
        }

        $this->poolCount = ord($d[12]);
        $this->poolIdxSize = ord($d[13]);
        $this->geoCount = $this->readU16(14);
        $this->rowCount = $this->readU32(20);
        $this->v4RecCount = $this->readU32(24);
        $this->v6RecCount = $this->readU32(28);

        $hs = $this->readU32(36);
        if ($hs !== 192) {
            throw new \RuntimeException("Unexpected header size: {$hs}");
        }

        // Offsets
        $this->offRowSchema = $this->readU64(40);
        $this->offGroupSchema = $this->readU64(48);
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
            throw new \RuntimeException('V4 jump table out of bounds');
        }
        if ($this->offV4Nodes + $this->v4NodeCount * 8 > $len) {
            throw new \RuntimeException('V4 nodes table out of bounds');
        }
        if ($this->offV6Jump + 65536 * 4 > $len) {
            throw new \RuntimeException('V6 jump table out of bounds');
        }
        if ($this->offV6Nodes + $this->v6NodeCount * 8 > $len) {
            throw new \RuntimeException('V6 nodes table out of bounds');
        }
        if ($this->offIPRow + $this->rowCount * $this->ipRowSize > $len) {
            throw new \RuntimeException('IP row table out of bounds');
        }
        if ($this->offGeoEntries + $this->geoEntryGroupCount * 20 > $len) {
            throw new \RuntimeException('Geo entries table out of bounds');
        }
        if ($this->offPools > $len) {
            throw new \RuntimeException('Pools table out of bounds');
        }
        if ($this->offMeta > $len) {
            throw new \RuntimeException('Meta table out of bounds');
        }

        // GeoEntryOffsets[4]
        $this->groupEntryOffsets = [];
        for ($i = 0; $i < 4; $i++) {
            $this->groupEntryOffsets[] = $this->readU48(168 + $i * 6);
        }

        // Parse GroupMetadataTable (at offGeoEntries)
        $gmOff = $this->offGeoEntries;
        $groupCount = ord($d[$gmOff]);
        $gmOff += 1;

        $actualGroups = min($groupCount, max(1, $this->geoEntryGroupCount));
        if ($actualGroups > 4) $actualGroups = 4;
        $this->groupFieldCounts = array_fill(0, $actualGroups, 0);
        $this->groupEntryCounts = array_fill(0, $actualGroups, 0);
        $this->groupDimMasks = array_fill(0, $actualGroups, 0);

        for ($gi = 0; $gi < $actualGroups; $gi++) {
            $this->groupFieldCounts[$gi] = ord($d[$gmOff]);
            $gmOff += 1;
            if ($fmtVer === 1 || $fmtVer >= 4) {
                $this->groupEntryCounts[$gi] = $this->readU32($gmOff);
                $gmOff += 4;
            } else {
                $this->groupEntryCounts[$gi] = $this->readU16($gmOff);
                $gmOff += 2;
            }

            if ($fmtVer === 1 || $fmtVer >= 3) {
                $this->groupDimMasks[$gi] = $this->readU16($gmOff);
                $gmOff += 2;
            } else {
                $this->groupDimMasks[$gi] = ($gi !== 2) ? 0x01 : 0x02;
            }
        }

        // Initialize schema and widths
        $this->groupStrides = array_fill(0, $actualGroups, 0);
        $this->groupFieldWidths = array_fill(0, $actualGroups, null);
        $this->groupFieldOffsets = array_fill(0, $actualGroups, null);
        $this->groupFieldNative = array_fill(0, $actualGroups, null);
        $this->groupFieldNativeType = array_fill(0, $actualGroups, null);

        // Parse GROUP_SCHEMA if present
        if ($this->offGroupSchema > 0) {
            $sp = $this->offGroupSchema;
            $gsGroupCount = $this->readU16($sp);
            $sp += 2;
            $maxGsGroups = min($gsGroupCount, $actualGroups);
            for ($gi = 0; $gi < $maxGsGroups; $gi++) {
                $sp += 2; // skip groupId
                $fldCount = $this->readU16($sp);
                $sp += 2;
                $sp += 4; // skip entryCount
                $stride = $this->readU32($sp);
                $sp += 4;
                $sp += 4; // skip flags

                if ($gi < $actualGroups) {
                    $this->groupStrides[$gi] = $stride;
                    $widths = array_fill(0, $fldCount, 0);
                    $offsets = array_fill(0, $fldCount, 0);
                    $natives = array_fill(0, $fldCount, false);
                    $natTypes = array_fill(0, $fldCount, 0);
                    $fieldIds = array_fill(0, $fldCount, 0);
                    $poolSectionIds = array_fill(0, $fldCount, 0);
                    for ($fi = 0; $fi < $fldCount; $fi++) {
                        $fieldIds[$fi] = $this->readU16($sp);
                        $sp += 2;
                        $widths[$fi] = ord($d[$sp]);
                        $sp += 1;
                        $fieldFlags = ord($d[$sp]);
                        $sp += 1;
                        $natives[$fi] = ($fieldFlags & 0x01) !== 0;
                        $natTypes[$fi] = ($fieldFlags >> 1) & 0x03;
                        $offsets[$fi] = $this->readU32($sp);
                        $sp += 4;
                        $poolSectionIds[$fi] = $this->readU32($sp);
                        $sp += 4;
                    }
                    $this->groupFieldWidths[$gi] = $widths;
                    $this->groupFieldOffsets[$gi] = $offsets;
                    $this->groupFieldNative[$gi] = $natives;
                    $this->groupFieldNativeType[$gi] = $natTypes;
                    $this->groupFieldIds[$gi] = $fieldIds;
                    $this->groupPoolSectionIds[$gi] = $poolSectionIds;
                } else {
                    $sp += $fldCount * 12;
                }
            }
        }

        // Fallback for groups without schema info
        for ($g = 0; $g < $actualGroups; $g++) {
            if ($this->groupStrides[$g] === 0) {
                $this->groupStrides[$g] = $this->groupFieldCounts[$g] * $this->poolIdxSize;
            }
            if ($this->groupFieldWidths[$g] === null) {
                $this->groupFieldWidths[$g] = array_fill(0, $this->groupFieldCounts[$g], $this->poolIdxSize);
            }
            if ($this->groupFieldOffsets[$g] === null) {
                $tempOffsets = [];
                for ($i = 0; $i < $this->groupFieldCounts[$g]; $i++) {
                    $tempOffsets[] = $i * $this->poolIdxSize;
                }
                $this->groupFieldOffsets[$g] = $tempOffsets;
            }
            if ($this->groupFieldNative[$g] === null) {
                $this->groupFieldNative[$g] = array_fill(0, $this->groupFieldCounts[$g], false);
            }
            if ($this->groupFieldNativeType[$g] === null) {
                $this->groupFieldNativeType[$g] = array_fill(0, $this->groupFieldCounts[$g], 0);
            }
        }

        $this->resolveFieldNames();
        $this->poolsLoaded = false;
        $this->groupPools = null;
    }

    private function resolveFieldNames()
    {
        $d = $this->data;
        $offMeta = $this->offMeta;
        if (($this->flags & 4) && $offMeta > 0 && $offMeta + 4 <= strlen($d)) {
            $fieldNames = null;
            $pos = $offMeta;
            while ($pos + 4 <= strlen($d)) {
                $t = ord($d[$pos]);
                $length = $this->readU16($pos + 2);
                if ($t === 0 || $length === 0) {
                    break;
                }
                $val = substr($d, $pos + 4, $length);
                if ($t === 1) {
                    $this->versionName = $val;
                } elseif ($t === 2) {
                    $fieldNames = explode('|', $val);
                }
                $pos += 4 + $length;
            }

            if ($fieldNames && count($fieldNames) === $this->groupFieldCounts[0]) {
                $this->fieldNames = $fieldNames;
                $this->floatFieldIndices = [];
                foreach ($fieldNames as $i => $n) {
                    if (isset(self::FLOAT_FIELDS[$n])) {
                        $this->floatFieldIndices[] = $n;
                    }
                }
                return;
            }
        }

        // Fallback placeholder names
        $this->fieldNames = [];
        for ($i = 0; $i < $this->groupFieldCounts[0]; $i++) {
            $this->fieldNames[] = "field_{$i}";
        }
        $this->floatFieldIndices = [];
    }

    private function ensurePoolsLoaded()
    {
        if ($this->poolsLoaded) {
            return;
        }
        $this->poolsLoaded = true;

        $groupCount = count($this->groupFieldCounts);
        $this->groupPools = array_fill(0, $groupCount, null);

        if ($this->offPools <= 0) {
            return;
        }

        $poolCursor = $this->offPools;
        $poolEnd = $this->offMeta > 0 ? $this->offMeta : strlen($this->data);
        $d = $this->data;

        for ($g = 0; $g < $groupCount; $g++) {
            $fieldCount = $this->groupFieldCounts[$g];
            $groupPoolList = [];
            $natives = $this->groupFieldNative[$g];
            for ($f = 0; $f < $fieldCount; $f++) {
                if ($natives && $f < count($natives) && $natives[$f]) {
                    $groupPoolList[] = [];
                    continue;
                }

                if ($poolCursor + 4 > $poolEnd) {
                    $groupPoolList[] = [];
                    continue;
                }
                $count = $this->readU32($poolCursor);
                $poolCursor += 4;
                if ($this->offRowSchema > 0) {
                    $poolCursor += 4;
                }
                if ($count === 0) {
                    $groupPoolList[] = [];
                    continue;
                }

                // Read string offsets
                $offsets = [];
                for ($o = 0; $o <= $count; $o++) {
                    $offsets[] = $this->readU32($poolCursor);
                    $poolCursor += 4;
                }

                // Read string data
                $strings = [];
                for ($s = 0; $s < $count; $s++) {
                    $start = $offsets[$s];
                    $end = $offsets[$s + 1];
                    $length = $end - $start;
                    if ($length > 0) {
                        $strings[] = substr($d, $poolCursor + $start, $length);
                    } else {
                        $strings[] = '';
                    }
                }
                $poolCursor += $offsets[$count];
                $groupPoolList[] = $strings;
            }
            $this->groupPools[$g] = $groupPoolList;
        }
    }

    private function getV4Child($nodeIdx, $bit)
    {
        if ($nodeIdx >= $this->v4NodeCount) return 0;
        if ($this->v4Node24) {
            $nodeOffset = $this->offV4Nodes + $nodeIdx * 6;
            $offset = $bit === 0 ? $nodeOffset : $nodeOffset + 3;
            $d = $this->data;
            $val = ord($d[$offset]) | (ord($d[$offset + 1]) << 8) | (ord($d[$offset + 2]) << 16);
            if ($val & 0x800000) {
                return ($val & 0x7FFFFF) | self::SENTINEL;
            }
            return $val;
        } else {
            $childOff = $this->offV4Nodes + $nodeIdx * 8 + $bit * 4;
            return $this->readU32($childOff);
        }
    }

    private function getV6Child($nodeIdx, $bit)
    {
        if ($nodeIdx >= $this->v6NodeCount) return 0;
        if ($this->v6Node24) {
            $nodeOffset = $this->offV6Nodes + $nodeIdx * 6;
            $offset = $bit === 0 ? $nodeOffset : $nodeOffset + 3;
            $d = $this->data;
            $val = ord($d[$offset]) | (ord($d[$offset + 1]) << 8) | (ord($d[$offset + 2]) << 16);
            if ($val & 0x800000) {
                return ($val & 0x7FFFFF) | self::SENTINEL;
            }
            return $val;
        } else {
            $childOff = $this->offV6Nodes + $nodeIdx * 8 + $bit * 4;
            return $this->readU32($childOff);
        }
    }

    private function trieWalkV4($ipInt)
    {
        $hi16 = ($ipInt >> 16) & 0xFFFF;
        $ptr = $this->readU32($this->offV4Jump + $hi16 * 4);

        if ($ptr === 0) {
            return 0;
        }
        if ($ptr & self::SENTINEL) {
            return $ptr & 0x7FFFFFFF;
        }

        $idx = $ptr;
        $suffix = ($ipInt & 0xFFFF) << 16;
        $steps = 0;

        while (true) {
            $bit = ($suffix >> 31) & 1;
            $child = $this->getV4Child($idx, $bit);

            if ($child === 0) {
                return 0;
            }
            if ($child & self::SENTINEL) {
                return $child & 0x7FFFFFFF;
            }

            $idx = $child;
            $suffix <<= 1;
            if (++$steps > 32) return 0;
        }
    }

    private function trieWalkV6(string $ipBin)
    {
        $v6_jump_bits = $this->v6JumpBits;
        
        $idx_jump = 0;
        $bits_collected = 0;
        for ($i = 0; $i < 16; $i++) {
            $byte = ord($ipBin[$i]);
            $bits_left = $v6_jump_bits - $bits_collected;
            if ($bits_left <= 0) {
                break;
            }
            if ($bits_left >= 8) {
                $idx_jump = ($idx_jump << 8) | $byte;
                $bits_collected += 8;
            } else {
                $idx_jump = ($idx_jump << $bits_left) | ($byte >> (8 - $bits_left));
                $bits_collected += $bits_left;
                break;
            }
        }

        $ptr = $this->readU32($this->offV6Jump + $idx_jump * 4);
        if ($ptr === 0) {
            return 0;
        }
        if ($ptr & self::SENTINEL) {
            return $ptr & 0x7FFFFFFF;
        }

        $idx = $ptr;
        $depth = $v6_jump_bits;

        while ($depth < 128) {
            $byteIdx = (int)($depth / 8);
            $bitIdx = 7 - ($depth % 8);
            $bit = (ord($ipBin[$byteIdx]) >> $bitIdx) & 1;

            $child = $this->getV6Child($idx, $bit);
            if ($child === 0) {
                return 0;
            }
            if ($child & self::SENTINEL) {
                return $child & 0x7FFFFFFF;
            }

            $idx = $child;
            $depth += 1;
        }

        return 0;
    }

    private function readIPRow($rowId)
    {
        if ($rowId <= 0 || $rowId >= $this->rowCount) {
            return [0, 0, 0];
        }
        $off = $this->offIPRow + $rowId * $this->ipRowSize;
        $geoId = $this->readU24($off);
        $asnId = $this->readU24($off + 3);

        $usageTypeId = 0;
        if ($this->ipRowSize >= 9) {
            $usageTypeId = $this->readU24($off + 6);
        }

        return [$geoId, $asnId, $usageTypeId];
    }

    private function resolveRowId($rowId, $groupIndex)
    {
        list($geoId, $asnId, $usageTypeId) = $this->readIPRow($rowId);
        $mask = $groupIndex < count($this->groupDimMasks) ? $this->groupDimMasks[$groupIndex] : 0;

        if ($mask & 0x02) {
            $entryId = $asnId;
        } elseif ($mask & 0x04) {
            $entryId = $usageTypeId;
        } else {
            $entryId = $geoId;
        }

        if ($entryId === 0) {
            return null;
        }
        return $this->resolveGeo($entryId, $groupIndex);
    }

    private function resolveGeo($entryId, $groupIndex)
    {
        if ($groupIndex < 0 || $groupIndex >= count($this->groupFieldCounts)) {
            return null;
        }
        if ($entryId < 0) {
            return null;
        }
        if ($entryId >= $this->groupEntryCounts[$groupIndex]) {
            return null;
        }

        $this->ensurePoolsLoaded();

        $fieldCount = $this->groupFieldCounts[$groupIndex];
        if ($fieldCount <= 0) {
            return null;
        }

        $groupEntryStart = $this->offGeoEntries + $this->groupEntryOffsets[$groupIndex];
        $stride = $this->groupStrides[$groupIndex];
        $entryOffset = $groupEntryStart + $entryId * $stride;
        $d = $this->data;

        $widths = $this->groupFieldWidths[$groupIndex];
        $baseOffsets = $this->groupFieldOffsets[$groupIndex];
        $natives = $this->groupFieldNative[$groupIndex];
        $natTypes = $this->groupFieldNativeType[$groupIndex];
        $fieldIds = isset($this->groupFieldIds[$groupIndex]) ? $this->groupFieldIds[$groupIndex] : null;
        $poolSectionIds = isset($this->groupPoolSectionIds[$groupIndex]) ? $this->groupPoolSectionIds[$groupIndex] : null;

        $fields = [];
        $resolvedFieldNames = [];
        for ($i = 0; $i < $fieldCount; $i++) {
            $w = $widths[$i];
            $fo = $entryOffset + $baseOffsets[$i];
            $isNative = $natives && $i < count($natives) && $natives[$i];

            if ($isNative) {
                $t = $natTypes && $i < count($natTypes) ? $natTypes[$i] : 0;
                if ($t === 1) {
                    // float
                    if ($w === 4) {
                        $valNum = unpack('f', substr($d, $fo, 4))[1];
                    } else {
                        $valNum = unpack('d', substr($d, $fo, 8))[1];
                    }
                    $val = sprintf('%.6f', $valNum);
                } else {
                    // int
                    $valNum = $this->readUintWidth($fo, $w);
                    $val = (string)$valNum;
                }
            } else {
                $idx = $this->readUintWidth($fo, $w);
                $groupPool = $this->groupPools[$groupIndex];
                
                // Use fieldId and poolSectionId if available
                $fieldId = ($fieldIds && $i < count($fieldIds)) ? $fieldIds[$i] : $i;
                $poolSectionId = ($poolSectionIds && $i < count($poolSectionIds)) ? $poolSectionIds[$i] : $i;
                
                // Bounds checks
                if ($fieldId >= count($this->fieldNames) || $poolSectionId >= count($groupPool[$i])) {
                    // Fall back to positional index
                    if ($groupPool && $i < count($groupPool) && $idx < count($groupPool[$i])) {
                        $val = $groupPool[$i][$idx];
                    } else {
                        $val = '';
                    }
                } else {
                    // Use fieldId and poolSectionId
                    $pool = $groupPool[$poolSectionId] ?? null;
                    if ($pool && $idx < count($pool)) {
                        $val = $pool[$idx];
                    } else {
                        $val = '';
                    }
                }
            }

            // Use fieldId for field name if available
            $fieldId = ($fieldIds && $i < count($fieldIds)) ? $fieldIds[$i] : $i;
            $fname = $fieldId < count($this->fieldNames) ? $this->fieldNames[$fieldId] : "field_{$fieldId}";
            $fields[$fname] = $val;
            $resolvedFieldNames[$i] = $fname;
        }

        return new GeoInfo($fields, $resolvedFieldNames, $this->floatFieldIndices);
    }

    public function find($ipStr)
    {
        if (!$ipStr) {
            return null;
        }

        if (strpos($ipStr, ':') !== false) {
            $packed = inet_pton($ipStr);
            if ($packed === false) {
                return null;
            }
            if (substr($packed, 0, 12) === "\x00\x00\x00\x00\x00\x00\x00\x00\x00\x00\xff\xff") {
                $ipInt = unpack('N', substr($packed, 12))[1];
                return $this->findUint($ipInt);
            }
            return $this->findV6Bin($packed);
        }

        $ipInt = self::fastParseIp($ipStr);
        if ($ipInt === null) {
            return null;
        }
        return $this->findUint($ipInt);
    }

    public function findUint($ipInt)
    {
        if (!$this->hasV4) {
            return null;
        }
        $rowId = $this->trieWalkV4($ipInt);
        if ($rowId === 0) {
            return null;
        }
        return $this->resolveRowId($rowId, $this->groupIndex);
    }

    public function findV6Bin($ipBin)
    {
        if (!$this->hasV6) {
            return null;
        }
        $rowId = $this->trieWalkV6($ipBin);
        if ($rowId === 0) {
            return null;
        }
        return $this->resolveRowId($rowId, $this->groupIndex);
    }

    public function findStr($ipStr)
    {
        $info = $this->find($ipStr);
        if ($info === null) {
            return '';
        }
        return $info->toPipe();
    }

    public function getFieldNames()
    {
        return $this->fieldNames;
    }

    public function getVersionCode()
    {
        $pcMap = [6 => 1, 7 => 2, 25 => 3];
        return $pcMap[$this->poolCount] ?? 3;
    }

    public function getPoolCount()
    {
        return $this->poolCount;
    }

    public function verifyCrc(): bool
    {
        if (strlen($this->data) < 20) {
            return false;
        }
        $stored = unpack('V', substr($this->data, 16, 4))[1];
        
        $original = substr($this->data, 16, 4);
        $this->data[16] = "\x00";
        $this->data[17] = "\x00";
        $this->data[18] = "\x00";
        $this->data[19] = "\x00";
        
        $computed = hexdec(hash('crc32b', $this->data));
        
        $this->data[16] = $original[0];
        $this->data[17] = $original[1];
        $this->data[18] = $original[2];
        $this->data[19] = $original[3];
        
        return $stored === $computed;
    }

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
}
