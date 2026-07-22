<?php
namespace Qqzeng\Ip;

class QzdbException extends \Exception
{
    public function __construct(string $message, int $code, ?\Throwable $previous = null)
    {
        parent::__construct($message, $code, $previous);
    }
}

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
    const SENTINEL_MASK_24 = 0x7FFFFF;
    const SENTINEL_MASK_31 = 0x7FFFFFFF;
    const FLOAT_FIELDS = ['longitude' => true, 'latitude' => true];
    const MAX_TRIE_WALK_STEPS = 1000;

    // Error codes
    const ERROR_NOT_FOUND = 1;
    const ERROR_CORRUPTED = 2;
    const ERROR_OUT_OF_BOUNDS = 3;
    const ERROR_INVALID_PARAM = 4;
    const ERROR_BAD_HEADER = 5;
    const ERROR_BAD_MAGIC = 6;
    const ERROR_UNSUPPORTED = 7;

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
            throw new QzdbException("Cannot read database file: " . $dbPath, self::ERROR_INVALID_PARAM);
        }
        $this->parseHeader();
    }

    private function safeReadU16($off)
    {
        return unpack('v', substr($this->data, $off, 2))[1];
    }

    private function safeReadU32($off)
    {
        return unpack('V', substr($this->data, $off, 4))[1];
    }

    private function safeReadU64($off)
    {
        return unpack('P', substr($this->data, $off, 8))[1];
    }

    private function safeReadU24($off)
    {
        $d = $this->data;
        return ord($d[$off]) | (ord($d[$off + 1]) << 8) | (ord($d[$off + 2]) << 16);
    }

    private function safeReadU48($off)
    {
        $d = $this->data;
        $low = unpack('V', substr($d, $off, 4))[1];
        $high = unpack('v', substr($d, $off + 4, 2))[1];
        return $low + ($high * 4294967296);
    }

    private function safeReadUintWidth($off, $width)
    {
        if ($width <= 1) {
            return ord($this->data[$off]);
        } elseif ($width == 2) {
            return $this->safeReadU16($off);
        } elseif ($width == 3) {
            return $this->safeReadU24($off);
        } else {
            return $this->safeReadU32($off);
        }
    }

    private function parseHeader()
    {
        $d = $this->data;
        if (strlen($d) < 192) {
            throw new QzdbException('File too small for QZDB header', self::ERROR_CORRUPTED);
        }

        $magic = substr($d, 0, 4);
        if ($magic !== 'QZDB') {
            throw new QzdbException('Invalid magic, expected QZDB', self::ERROR_BAD_MAGIC);
        }

        $fmtVer = ord($d[4]);
        if ($fmtVer < 1 || $fmtVer > 6) {
            throw new QzdbException("Unsupported format version: {$fmtVer}", self::ERROR_UNSUPPORTED);
        }

        $this->flags = $this->safeReadU16(8);
        $this->hasV4 = (bool)($this->flags & 1);
        $this->hasV6 = (bool)($this->flags & 2);
        $this->v4Node24 = (bool)($this->flags & 0x10);
        $this->v6Node24 = (bool)($this->flags & 0x20);

        $this->v6JumpBits = ord($d[11]);
        if ($this->v6JumpBits === 0) {
            $this->v6JumpBits = 16;
        }
        if ($this->v6JumpBits < 16 || $this->v6JumpBits > 20) {
            throw new QzdbException("v6JumpBits out of range [16,20]: {$this->v6JumpBits}", self::ERROR_CORRUPTED);
        }

        $this->poolCount = ord($d[12]);
        $this->poolIdxSize = ord($d[13]);
        if ($this->poolIdxSize !== 2 && $this->poolIdxSize !== 3) {
            throw new QzdbException("poolIdxSize must be 2 or 3, got {$this->poolIdxSize}", self::ERROR_CORRUPTED);
        }
        $this->geoCount = $this->safeReadU16(14);
        $this->rowCount = $this->safeReadU32(20);
        $this->v4RecCount = $this->safeReadU32(24);
        $this->v6RecCount = $this->safeReadU32(28);

        $hs = $this->safeReadU32(36);
        if ($hs !== 192) {
            throw new QzdbException("Unexpected header size: {$hs}", self::ERROR_CORRUPTED);
        }

        // Offsets
        $this->offRowSchema = $this->safeReadU64(40);
        $this->offGroupSchema = $this->safeReadU64(48);
        $this->offV4Jump = $this->safeReadU64(64);
        $this->offV4Nodes = $this->safeReadU64(72);
        $this->offV6Jump = $this->safeReadU64(80);
        $this->offV6Nodes = $this->safeReadU64(88);
        $this->offIPRow = $this->safeReadU64(96);
        $this->offGeoEntries = $this->safeReadU64(104);
        $this->offPools = $this->safeReadU64(136);
        $this->offMeta = $this->safeReadU64(144);

        $this->v4NodeCount = $this->safeReadU32(152);
        $this->v6NodeCount = $this->safeReadU32(156);
        $this->ipRowSize = $this->safeReadU32(160);
        if ($this->ipRowSize < 1 || $this->ipRowSize > 64) {
            throw new QzdbException("ipRowSize out of range [1,64]: {$this->ipRowSize}", self::ERROR_CORRUPTED);
        }
        $this->geoEntryGroupCount = $this->safeReadU32(164);
        if ($this->geoEntryGroupCount < 1 || $this->geoEntryGroupCount > 255) {
            throw new QzdbException("geoEntryGroupCount out of range [1,255]: {$this->geoEntryGroupCount}", self::ERROR_CORRUPTED);
        }

        $d = $this->data;
        $len = strlen($d);
        $v4NodeSize = $this->v4Node24 ? 6 : 8;
        $v6NodeSize = $this->v6Node24 ? 6 : 8;
        $v6JumpSize = (1 << $this->v6JumpBits) * 4;

        if ($this->offV4Jump > 0 && $this->offV4Jump + 65536 * 4 > $len) {
            throw new QzdbException('V4 jump table out of bounds', self::ERROR_OUT_OF_BOUNDS);
        }
        if ($this->offV4Nodes > 0 && $this->offV4Nodes + $this->v4NodeCount * $v4NodeSize > $len) {
            throw new QzdbException('V4 nodes table out of bounds', self::ERROR_OUT_OF_BOUNDS);
        }
        if ($this->offV6Jump > 0 && $this->offV6Jump + $v6JumpSize > $len) {
            throw new QzdbException('V6 jump table out of bounds', self::ERROR_OUT_OF_BOUNDS);
        }
        if ($this->offV6Nodes > 0 && $this->offV6Nodes + $this->v6NodeCount * $v6NodeSize > $len) {
            throw new QzdbException('V6 nodes table out of bounds', self::ERROR_OUT_OF_BOUNDS);
        }
        if ($this->offIPRow > 0 && $this->offIPRow + $this->rowCount * $this->ipRowSize > $len) {
            throw new QzdbException('IP row table out of bounds', self::ERROR_OUT_OF_BOUNDS);
        }

        // GeoEntryOffsets[4]
        $this->groupEntryOffsets = [];
        for ($i = 0; $i < 4; $i++) {
            $this->groupEntryOffsets[] = $this->safeReadU48(168 + $i * 6);
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
                $this->groupEntryCounts[$gi] = $this->safeReadU32($gmOff);
                $gmOff += 4;
            } else {
                $this->groupEntryCounts[$gi] = $this->safeReadU16($gmOff);
                $gmOff += 2;
            }

            if ($fmtVer === 1 || $fmtVer >= 3) {
                $this->groupDimMasks[$gi] = $this->safeReadU16($gmOff);
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
            $gsGroupCount = $this->safeReadU16($sp);
            $sp += 2;
            $maxGsGroups = min($gsGroupCount, $actualGroups);
            for ($gi = 0; $gi < $maxGsGroups; $gi++) {
                $sp += 2; // skip groupId
                $fldCount = $this->safeReadU16($sp);
                $sp += 2;
                $sp += 4; // skip entryCount
                $stride = $this->safeReadU32($sp);
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
                        $fieldIds[$fi] = $this->safeReadU16($sp);
                        $sp += 2;
                        $widths[$fi] = ord($d[$sp]);
                        $sp += 1;
                        $fieldFlags = ord($d[$sp]);
                        $sp += 1;
                        $natives[$fi] = ($fieldFlags & 0x01) !== 0;
                        $natTypes[$fi] = ($fieldFlags >> 1) & 0x03;
                        $offsets[$fi] = $this->safeReadU32($sp);
                        $sp += 4;
                        $poolSectionIds[$fi] = $this->safeReadU32($sp);
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
                $length = $this->safeReadU16($pos + 2);
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
                $count = $this->safeReadU32($poolCursor);
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
                    $offsets[] = $this->safeReadU32($poolCursor);
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
                return ($val & self::SENTINEL_MASK_24) | self::SENTINEL;
            }
            return $val;
        } else {
            $childOff = $this->offV4Nodes + $nodeIdx * 8 + $bit * 4;
            return $this->safeReadU32($childOff);
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
                return ($val & self::SENTINEL_MASK_24) | self::SENTINEL;
            }
            return $val;
        } else {
            $childOff = $this->offV6Nodes + $nodeIdx * 8 + $bit * 4;
            return $this->safeReadU32($childOff);
        }
    }

    private function trieWalkV4($ipInt)
    {
        $hi16 = ($ipInt >> 16) & 0xFFFF;
        $ptr = $this->safeReadU32($this->offV4Jump + $hi16 * 4);

        if ($ptr === 0) {
            return 0;
        }
        if ($ptr & self::SENTINEL) {
            return $ptr & self::SENTINEL_MASK_31;
        }

        $idx = $ptr;
        $suffix = ($ipInt & 0xFFFF) << 16;
        $steps = 0;

        while (true) {
            if (++$steps > 32) return 0;
            $bit = ($suffix >> 31) & 1;
            $child = $this->getV4Child($idx, $bit);

            if ($child === 0) {
                return 0;
            }
            if ($child & self::SENTINEL) {
                return $child & self::SENTINEL_MASK_31;
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

        $ptr = $this->safeReadU32($this->offV6Jump + $idx_jump * 4);
        if ($ptr === 0) {
            return 0;
        }
        if ($ptr & self::SENTINEL) {
            return $ptr & self::SENTINEL_MASK_31;
        }

        $idx = $ptr;
        $depth = $v6_jump_bits;
        $steps = 0;

        while ($depth < 128) {
            if (++$steps >= self::MAX_TRIE_WALK_STEPS) {
                return 0;
            }
            $byteIdx = (int)($depth / 8);
            $bitIdx = 7 - ($depth % 8);
            $bit = (ord($ipBin[$byteIdx]) >> $bitIdx) & 1;

            $child = $this->getV6Child($idx, $bit);
            if ($child === 0) {
                return 0;
            }
            if ($child & self::SENTINEL) {
                return $child & self::SENTINEL_MASK_31;
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
        $geoId = $this->safeReadU24($off);
        $asnId = $this->safeReadU24($off + 3);

        $usageTypeId = 0;
        if ($this->ipRowSize >= 9) {
            $usageTypeId = $this->safeReadU24($off + 6);
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
                    $valNum = $this->safeReadUintWidth($fo, $w);
                    $val = (string)$valNum;
                }
            } else {
                $idx = $this->safeReadUintWidth($fo, $w);
                $groupPool = $this->groupPools[$groupIndex];
                
                // Use positional index for pool lookup (poolSectionId is metadata only)
                if ($groupPool && $i < count($groupPool) && $idx < count($groupPool[$i])) {
                    $val = $groupPool[$i][$idx];
                } else {
                    $val = '';
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
        if (!$ipStr) return null;
        $result = self::fastParseIp($ipStr);
        if ($result === null) return null;
        list($v4, $v6) = $result;
        if ($v4 !== null) return $this->findUint($v4);
        if (!$this->hasV6) return null;
        return $this->findV6Bin($v6);
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

    public function lookupRowId($ipStr)
    {
        if ($ipStr === null || $ipStr === '') return 0;
        $result = self::fastParseIp($ipStr);
        if ($result === null) return 0;
        list($v4, $v6) = $result;
        if ($v4 !== null) return $this->lookupRowIdUint($v4);
        return $this->lookupRowIdV6($v6);
    }

    public function lookupRowIdUint($ipInt)
    {
        if (!$this->hasV4) return 0;
        return $this->trieWalkV4($ipInt);
    }

    public function lookupRowIdV6($ipBin)
    {
        if (!$this->hasV6) return 0;
        return $this->trieWalkV6($ipBin);
    }

    public function lookupIds($rowId)
    {
        if ($rowId <= 0 || $rowId >= $this->rowCount) return null;
        $row = $this->readIPRow($rowId);
        return [$row[0], $row[1], $row[2]];
    }

    public function findStr($ipStr)
    {
        $info = $this->find($ipStr);
        if ($info === null) {
            return '';
        }
        return $info->toPipe();
    }

    public function findFields($ipStr, $fieldNames = null)
    {
        if ($fieldNames === null || count($fieldNames) === 0) {
            return $this->find($ipStr);
        }
        $rowId = $this->lookupRowId($ipStr);
        if ($rowId === 0) return null;
        return $this->resolveGeoFields($rowId, $this->groupIndex, $fieldNames);
    }

    private function resolveGeoFields($rowId, $groupIndex, $fieldNames)
    {
        list($geoId, $asnId, $usageTypeId) = $this->readIPRow($rowId);
        $mask = $groupIndex < count($this->groupDimMasks) ? $this->groupDimMasks[$groupIndex] : 0;
        $entryId = ($mask & 0x02) ? $asnId : (($mask & 0x04) ? $usageTypeId : $geoId);
        if ($entryId === 0 || $groupIndex < 0 || $groupIndex >= count($this->groupFieldCounts)) return null;
        if ($entryId >= $this->groupEntryCounts[$groupIndex]) return null;

        $this->ensurePoolsLoaded();
        $fieldCount = $this->groupFieldCounts[$groupIndex];
        if ($fieldCount <= 0) return null;

        $nameToIdx = [];
        foreach ($this->fieldNames as $i => $name) {
            $nameToIdx[$name] = $i;
        }
        $indices = [];
        foreach ($fieldNames as $name) {
            if (isset($nameToIdx[$name])) $indices[] = $nameToIdx[$name];
        }
        if (count($indices) === 0) return null;

        $groupEntryStart = $this->offGeoEntries + $this->groupEntryOffsets[$groupIndex];
        $stride = $this->groupStrides[$groupIndex];
        $entryOffset = $groupEntryStart + $entryId * $stride;
        $d = $this->data;
        $widths = $this->groupFieldWidths[$groupIndex];
        $baseOffsets = $this->groupFieldOffsets[$groupIndex];
        $natives = $this->groupFieldNative[$groupIndex];
        $natTypes = $this->groupFieldNativeType[$groupIndex];
        $fieldIds = isset($this->groupFieldIds[$groupIndex]) ? $this->groupFieldIds[$groupIndex] : null;

        $resolved = [];
        foreach ($indices as $i) {
            if ($i < 0 || $i >= $fieldCount) continue;
            $w = $widths[$i];
            $fo = $entryOffset + $baseOffsets[$i];
            $isNative = $natives && $i < count($natives) && $natives[$i];
            if ($isNative) {
                $t = $natTypes && $i < count($natTypes) ? $natTypes[$i] : 0;
                if ($t === 1) {
                    $valNum = $w === 4 ? unpack('f', substr($d, $fo, 4))[1] : unpack('d', substr($d, $fo, 8))[1];
                    $resolved[$i] = sprintf('%.6f', $valNum);
                } else {
                    $resolved[$i] = (string)$this->safeReadUintWidth($fo, $w);
                }
            } else {
                $idx = $this->safeReadUintWidth($fo, $w);
                $groupPool = $this->groupPools[$groupIndex];
                $resolved[$i] = ($groupPool && $i < count($groupPool) && $idx < count($groupPool[$i])) ? $groupPool[$i][$idx] : '';
            }
        }

        $fields = [];
        $resolvedFieldNames = [];
        for ($i = 0; $i < $fieldCount; $i++) {
            $fieldId = ($fieldIds && $i < count($fieldIds)) ? $fieldIds[$i] : $i;
            $fname = $fieldId < count($this->fieldNames) ? $this->fieldNames[$fieldId] : "field_{$fieldId}";
            $val = isset($resolved[$i]) ? $resolved[$i] : '';
            $fields[$fname] = $val;
            $resolvedFieldNames[$i] = $fname;
        }
        return new GeoInfo($fields, $resolvedFieldNames, $this->floatFieldIndices);
    }

    public function reload($dbPath)
    {
        $this->load($dbPath);
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
        
        $copy = $this->data;
        $copy[16] = "\x00";
        $copy[17] = "\x00";
        $copy[18] = "\x00";
        $copy[19] = "\x00";
        
        $computed = hexdec(hash('crc32b', $copy));
        
        return $stored === $computed;
    }

    private static $HEX = null;

    private static function initHex()
    {
        if (self::$HEX !== null) return;
        self::$HEX = array_fill(0, 128, 0);
        for ($i = 0; $i < 10; $i++) self::$HEX[48 + $i] = $i;
        for ($i = 0; $i < 6; $i++) { self::$HEX[97 + $i] = 10 + $i; self::$HEX[65 + $i] = 10 + $i; }
    }

    private static function fastParseIpv4($s)
    {
        $n = strlen($s);
        if ($n === 0 || $s[$n - 1] === '.') return null;
        $result = 0; $val = 0; $dots = 0; $start = 0;
        for ($i = 0; $i <= $n; $i++) {
            $c = $i < $n ? ord($s[$i]) : 46;
            if ($c === 46) {
                $segLen = $i - $start;
                if ($segLen === 0 || $segLen > 3) return null;
                if ($segLen > 1 && $s[$start] === '0') return null;
                $val = 0;
                for ($j = $start; $j < $i; $j++) {
                    $d = ord($s[$j]);
                    if ($d < 48 || $d > 57) return null;
                    $val = $val * 10 + ($d - 48);
                }
                if ($val > 255) return null;
                $result = ($result << 8) | $val;
                $dots++; $start = $i + 1;
            }
        }
        return $dots === 4 ? $result : null;
    }

    private static function fastParseIp($ip)
    {
        if (!is_string($ip)) return null;
        // Fail-closed: reject any whitespace (no silent trim — SSRF-safe, cross-lang consistent)
        for ($i = 0, $n = strlen($ip); $i < $n; $i++) {
            $c = $ip[$i];
            if ($c === ' ' || $c === "\t" || $c === "\n" || $c === "\r" || $c === "\v" || $c === "\f") {
                return null;
            }
        }
        if ($n === 0 || $n > 45) return null;
        $s = $ip;
        if (strpos($s, ':') === false) {
            $v4 = self::fastParseIpv4($s);
            return $v4 !== null ? array($v4, null) : null;
        }
        if (strpos($s, '%') !== false) return null;
        $dc = strpos($s, '::');
        if ($dc !== false && strpos($s, '::', $dc + 2) !== false) return null;
        $lft = $dc !== false ? substr($s, 0, $dc) : $s;
        $rgt = $dc !== false ? substr($s, $dc + 2) : '';
        $lg = $lft !== '' ? explode(':', $lft) : array();
        $rg = $rgt !== '' ? explode(':', $rgt) : array();
        if ($lg === array('')) $lg = array();
        if ($rg === array('')) $rg = array();
        foreach ($lg as $g) { if ($g === '') return null; }
        foreach ($rg as $g) { if ($g === '') return null; }
        $allg = array_merge($lg, $rg);
        $hasV4 = false; $v4Int = 0;
        $last = count($allg) - 1;
        if ($last >= 0 && strpos($allg[$last], '.') !== false) {
            $v4Int = self::fastParseIpv4($allg[$last]);
            if ($v4Int === null) return null;
            $hasV4 = true;
            array_pop($allg);
        }
        $ng = count($allg);
        $v4Slots = $hasV4 ? 2 : 0;
        if ($dc !== false) {
            if ($ng + $v4Slots > 7) return null;
        } else {
            if ($ng + $v4Slots !== 8) return null;
        }
        self::initHex();
        foreach ($allg as $g) {
            $gl = strlen($g);
            if ($gl === 0 || $gl > 4) return null;
            for ($j = 0; $j < $gl; $j++) {
                $cc = ord($g[$j]);
                if ($cc >= 128 || (self::$HEX[$cc] === 0 && $cc !== 48)) return null;
            }
        }
        $zeros = 8 - $ng - $v4Slots;
        $buf = str_repeat("\0", 16);
        $off = 0;
        foreach ($lg as $g) {
            $v = 0;
            for ($j = 0; $j < strlen($g); $j++) $v = ($v << 4) | self::$HEX[ord($g[$j])];
            $buf[$off] = chr($v >> 8); $buf[$off + 1] = chr($v & 0xff);
            $off += 2;
        }
        $off += $zeros * 2;
        foreach ($rg as $g) {
            $v = 0;
            for ($j = 0; $j < strlen($g); $j++) $v = ($v << 4) | self::$HEX[ord($g[$j])];
            $buf[$off] = chr($v >> 8); $buf[$off + 1] = chr($v & 0xff);
            $off += 2;
        }
        if ($hasV4) { $buf[12] = chr(($v4Int >> 24) & 0xff); $buf[13] = chr(($v4Int >> 16) & 0xff); $buf[14] = chr(($v4Int >> 8) & 0xff); $buf[15] = chr($v4Int & 0xff); }
        if (ord($buf[10]) === 0xff && ord($buf[11]) === 0xff && ord($buf[0]) === 0 && ord($buf[1]) === 0 && ord($buf[2]) === 0 && ord($buf[3]) === 0 && ord($buf[4]) === 0 && ord($buf[5]) === 0 && ord($buf[6]) === 0 && ord($buf[7]) === 0 && ord($buf[8]) === 0 && ord($buf[9]) === 0) {
            return array(((ord($buf[12]) << 24) | (ord($buf[13]) << 16) | (ord($buf[14]) << 8) | ord($buf[15])) & 0xffffffff, null);
        }
        return array(null, $buf);
    }
}
