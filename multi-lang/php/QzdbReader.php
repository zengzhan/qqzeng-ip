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
    private $values;
    private $fieldNames;
    private $floatIndices;

    public function __construct(array $values = [], array $fieldNames = [], array $floatIndices = [])
    {
        $this->values = $values;
        $this->fieldNames = $fieldNames;
        $this->floatIndices = array_flip($floatIndices);
    }

    public function __get($name)
    {
        $idx = array_search($name, $this->fieldNames, true);
        return ($idx !== false && $idx < count($this->values)) ? $this->values[$idx] : '';
    }

    public function get($name)
    {
        $idx = array_search($name, $this->fieldNames, true);
        return ($idx !== false && $idx < count($this->values)) ? $this->values[$idx] : '';
    }

    public function offsetExists($offset): bool
    {
        if (is_int($offset)) {
            return $offset >= 0 && $offset < count($this->values);
        }
        $idx = array_search($offset, $this->fieldNames, true);
        return $idx !== false;
    }

    #[\ReturnTypeWillChange]
    public function offsetGet($offset)
    {
        if (is_int($offset)) {
            return $this->values[$offset] ?? '';
        }
        $idx = array_search($offset, $this->fieldNames, true);
        return ($idx !== false && $idx < count($this->values)) ? $this->values[$idx] : '';
    }

    public function offsetSet($offset, $value): void
    {
        if (is_int($offset)) {
            $this->values[$offset] = $value;
        } else {
            $idx = array_search($offset, $this->fieldNames, true);
            if ($idx !== false) {
                $this->values[$idx] = $value;
            }
        }
    }

    public function offsetUnset($offset): void
    {
        if (is_int($offset)) {
            unset($this->values[$offset]);
        } else {
            $idx = array_search($offset, $this->fieldNames, true);
            if ($idx !== false) {
                unset($this->values[$idx]);
            }
        }
    }

    public static function formatFloatValue($val)
    {
        if (is_finite($val) && floor($val) == $val) {
            return (string)(int)$val;
        }
        $s = json_encode($val);
        return ($s === false) ? (string)$val : $s;
    }

    public function toPipe()
    {
        $parts = [];
        foreach ($this->fieldNames as $i => $fname) {
            $val = $this->values[$i] ?? '';
            if (isset($this->floatIndices[$fname]) && $val !== '') {
                $val = self::formatFloatValue((float)$val);
            }
            $parts[] = (string)$val;
        }
        return implode('|', $parts);
    }
}

class QzdbReader
{
    private static $instance = null;
    private $data;            // in-memory buffer (null when streaming)
    private $stream = null;   // fopen() handle when file is too large to buffer
    private $fileSize = 0;    // total file size in bytes
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

    private $rowGeoWidth = 3;
    private $rowAsnWidth = 3;
    private $rowUsageWidth = 0;

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

    // Lazy pool model: per (group, field) descriptor [ot, db, count] or null for native fields.
    // Strings are resolved on demand via poolString() — never materialized into PHP arrays.
    private $groupPoolDescs = null;
    private $poolsLoaded = false;

    const SENTINEL = 0x80000000;
    const SENTINEL_MASK_24 = 0x7FFFFF;
    const SENTINEL_MASK_31 = 0x7FFFFFFF;
    const FLOAT_FIELDS = ['longitude' => true, 'latitude' => true];
    const MAX_TRIE_WALK_STEPS = 1000;
    const MAX_POOL_COUNT = 1 << 26;

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

    public function __destruct()
    {
        if ($this->stream !== null && is_resource($this->stream)) {
            @fclose($this->stream);
            $this->stream = null;
        }
    }

    public function load($dbPath)
    {
        $size = @filesize($dbPath);
        if ($size === false) {
            throw new QzdbException("Cannot stat database file: " . $dbPath, self::ERROR_INVALID_PARAM);
        }
        $this->fileSize = $size;

        // Adaptive storage: if the file is larger than half the PHP memory_limit,
        // buffering it in memory would risk OOM (files now routinely exceed 128MB).
        // In that case we keep only a stream handle and read on demand via fseek/fread,
        // so peak memory stays O(1) regardless of file size. Smaller files are buffered
        // for speed (the previous behaviour) — both paths go through readBytes(), so the
        // parsed result is byte-identical.
        $memLimit = $this->parseMemoryLimitBytes();
        if ($memLimit > 0 && $size > (int)($memLimit * 0.5)) {
            $this->stream = @fopen($dbPath, 'rb');
            if ($this->stream === false || $this->stream === null) {
                throw new QzdbException("Cannot open database file: " . $dbPath, self::ERROR_INVALID_PARAM);
            }
            $this->data = null;
        } else {
            $this->data = @file_get_contents($dbPath);
            if ($this->data === false) {
                throw new QzdbException("Cannot read database file: " . $dbPath, self::ERROR_INVALID_PARAM);
            }
            $this->stream = null;
        }

        $this->parseHeader();
        if (!$this->verifyCrc()) {
            throw new QzdbException('CRC32 checksum mismatch — the .qzdb file is corrupted or truncated', self::ERROR_CORRUPTED);
        }
    }

    /**
     * Resolve PHP memory_limit (e.g. "128M", "2G", "-1") to bytes.
     * Returns 0 when unlimited (-1) so the caller falls back to buffering.
     */
    private function parseMemoryLimitBytes()
    {
        $raw = trim((string)ini_get('memory_limit'));
        if ($raw === '' || $raw === '-1') {
            return 0;
        }
        $unit = strtolower($raw[strlen($raw) - 1]);
        $num = (int)$raw;
        switch ($unit) {
            case 'g': $num *= 1024; break;
            case 'm': $num *= 1024; break;
            case 'k': $num *= 1024; break;
        }
        return $num;
    }

    /**
     * Unified byte reader. When streaming, reads [$off, $off+$len) from the file
     * handle; otherwise slices the in-memory buffer. Single source of truth so the
     * parse logic is identical whether or not the file is buffered.
     */
    private function readBytes($off, $len)
    {
        if ($len <= 0) {
            return '';
        }
        if ($this->stream !== null) {
            if ($off < 0) {
                return '';
            }
            if (@fseek($this->stream, $off, SEEK_SET) !== 0) {
                return '';
            }
            $b = @fread($this->stream, $len);
            return ($b === false) ? '' : $b;
        }
        if ($this->data === null || $off < 0) {
            return '';
        }
        $avail = strlen($this->data) - $off;
        if ($avail <= 0) {
            return '';
        }
        if ($len > $avail) {
            $len = $avail;
        }
        return substr($this->data, $off, $len);
    }

    private function readByte($off)
    {
        $b = $this->readBytes($off, 1);
        return $b === '' ? 0 : ord($b);
    }

    private function safeReadU16($off)
    {
        if ($off < 0 || $off + 2 > strlen($this->data)) {
            throw new QzdbException('Out of bounds reading U16 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
        }
        return unpack('v', $this->data, $off)[1];
    }

    private function safeReadU32($off)
    {
        if ($off < 0 || $off + 4 > strlen($this->data)) {
            throw new QzdbException('Out of bounds reading U32 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
        }
        return unpack('V', $this->data, $off)[1];
    }

    private function safeReadU64($off)
    {
        if ($off < 0 || $off + 8 > strlen($this->data)) {
            throw new QzdbException('Out of bounds reading U64 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
        }
        return unpack('P', $this->data, $off)[1];
    }

    private function safeReadU24($off)
    {
        if ($off < 0 || $off + 3 > strlen($this->data)) {
            throw new QzdbException('Out of bounds reading U24 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
        }
        return ord($this->data[$off]) | (ord($this->data[$off + 1]) << 8) | (ord($this->data[$off + 2]) << 16);
    }

    private function safeReadU48($off)
    {
        if ($off < 0 || $off + 6 > strlen($this->data)) {
            throw new QzdbException('Out of bounds reading U48 at offset ' . $off, self::ERROR_OUT_OF_BOUNDS);
        }
        $low = unpack('V', $this->data, $off)[1];
        $high = unpack('v', $this->data, $off + 4)[1];
        return $low + ($high * 4294967296);
    }

    private function safeReadUintWidth($off, $width)
    {
        if ($width <= 1) {
            return $this->readByte($off);
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
        if ($this->fileSize < 192) {
            throw new QzdbException('File too small for QZDB header', self::ERROR_CORRUPTED);
        }

        $magic = $this->readBytes(0, 4);
        if ($magic !== 'QZDB') {
            throw new QzdbException('Invalid magic, expected QZDB', self::ERROR_BAD_MAGIC);
        }

        // Spec §10.1: QZDBReader accepts only format version 1.
        // All in-repo real QZDB fixtures are v1; reject anything else.
        $fmtVer = $this->readByte(4);
        if ($fmtVer !== 1) {
            throw new QzdbException("Unsupported format version: {$fmtVer} (only version 1 is supported)", self::ERROR_UNSUPPORTED);
        }

        $this->flags = $this->safeReadU16(8);
        $this->hasV4 = (bool)($this->flags & 1);
        $this->hasV6 = (bool)($this->flags & 2);
        $this->v4Node24 = (bool)($this->flags & 0x10);
        $this->v6Node24 = (bool)($this->flags & 0x20);

        // Spec §4.2: v6JumpBits valid range is [8,20]. Real .qzdb fixtures use up to 20.
        $this->v6JumpBits = $this->readByte(11);
        if ($this->v6JumpBits === 0) {
            $this->v6JumpBits = 16;
        }
        if ($this->v6JumpBits < 8 || $this->v6JumpBits > 20) {
            throw new QzdbException("v6JumpBits out of range [8,20]: {$this->v6JumpBits}", self::ERROR_CORRUPTED);
        }

        $this->poolCount = $this->readByte(12);
        $this->poolIdxSize = $this->readByte(13);
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

        $this->parseRowSchema();

        $len = $this->fileSize;
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
        $groupCount = $this->readByte($gmOff);
        $gmOff += 1;

        $actualGroups = min($groupCount, max(1, $this->geoEntryGroupCount));
        if ($actualGroups > 4) $actualGroups = 4;
        $this->groupFieldCounts = array_fill(0, $actualGroups, 0);
        $this->groupEntryCounts = array_fill(0, $actualGroups, 0);
        $this->groupDimMasks = array_fill(0, $actualGroups, 0);

        // §6.2 GroupMetadataTable FIXED layout per group (no version-dependent widths):
        //   1 byte  fieldCount
        //   4 bytes uint32 LE entryCount
        //   2 bytes uint16 LE dimensionMask
        // The old code branched on fmtVer and fell back to a hard-coded
        // ($gi !== 2) ? 0x01 : 0x02 mask that is wrong for ASN databases; we now
        // read the layout verbatim and repair a zero mask from metadata (see repairDimMasks).
        for ($gi = 0; $gi < $actualGroups; $gi++) {
            $this->groupFieldCounts[$gi] = $this->readByte($gmOff);
            $gmOff += 1;
            $this->groupEntryCounts[$gi] = $this->safeReadU32($gmOff);
            $gmOff += 4;
            $this->groupDimMasks[$gi] = $this->safeReadU16($gmOff);
            $gmOff += 2;
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
                        $widths[$fi] = $this->readByte($sp);
                        $sp += 1;
                        $fieldFlags = $this->readByte($sp);
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
        $this->repairDimMasks();
        $this->poolsLoaded = false;
        $this->groupPoolDescs = null;
    }

    private function resolveFieldNames()
    {
        $offMeta = $this->offMeta;
        if (($this->flags & 4) && $offMeta > 0 && $offMeta + 4 <= $this->fileSize) {
            $fieldNames = null;
            $pos = $offMeta;
            while ($pos + 4 <= $this->fileSize) {
                $t = $this->readByte($pos);
                $length = $this->safeReadU16($pos + 2);
                if ($t === 0 || $length === 0) {
                    break;
                }
                $val = $this->readBytes($pos + 4, $length);
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

    /**
     * Derives a sensible dimensionMask for any group whose mask was stored as 0
     * (should not happen in valid files). Bit0(0x01)=geo, Bit1(0x02)=asn. A group
     * is treated as ASN-addressed when its group schema or field names expose an
     * "asn" field (fieldId 1); otherwise geo-addressed. This replaces the old
     * hard-coded ($gi !== 2) ? 0x01 : 0x02 fallback that produced wrong results on
     * ASN databases.
     */
    private function repairDimMasks()
    {
        $n = count($this->groupDimMasks);
        for ($g = 0; $g < $n; $g++) {
            if ($this->groupDimMasks[$g] !== 0) {
                continue;
            }
            $hasAsn = false;
            if (isset($this->groupFieldIds[$g]) && is_array($this->groupFieldIds[$g])) {
                foreach ($this->groupFieldIds[$g] as $fid) {
                    if ($fid == 1) {
                        $hasAsn = true;
                        break;
                    }
                }
            }
            if (!$hasAsn && is_array($this->fieldNames)) {
                foreach ($this->fieldNames as $n2) {
                    if ($n2 === 'asn') {
                        $hasAsn = true;
                        break;
                    }
                }
            }
            $this->groupDimMasks[$g] = $hasAsn ? 0x02 : 0x01;
        }
    }

    private function ensurePoolsLoaded()
    {
        if ($this->poolsLoaded) {
            return;
        }
        $this->poolsLoaded = true;

        $groupCount = count($this->groupFieldCounts);
        $this->groupPoolDescs = array_fill(0, $groupCount, []);

        if ($this->offPools <= 0) {
            return;
        }

        $poolCursor = $this->offPools;
        $poolEnd = $this->offMeta > 0 ? $this->offMeta : $this->fileSize;

        for ($g = 0; $g < $groupCount; $g++) {
            $fieldCount = $this->groupFieldCounts[$g];
            $groupDescs = [];
            $natives = $this->groupFieldNative[$g];
            for ($f = 0; $f < $fieldCount; $f++) {
                if ($natives && $f < count($natives) && $natives[$f]) {
                    // Native field: value is stored inline in the GeoEntry row, no pool.
                    $groupDescs[] = null;
                    continue;
                }

                if ($poolCursor + 4 > $poolEnd) {
                    $groupDescs[] = null;
                    continue;
                }
                $count = $this->safeReadU32($poolCursor);
                $poolCursor += 4;
                if ($this->offRowSchema > 0) {
                    $poolCursor += 4;
                }
                // Security guard: unbounded count would OOM on count+1 offsets.
                if ($count === 0 || $count > self::MAX_POOL_COUNT) {
                    $groupDescs[] = null;
                    continue;
                }

                // Lazy model: keep only the offset-table base, string-data base and entry count.
                // Strings are resolved on demand by poolString() — never copied into PHP arrays,
                // so peak memory stays at O(file buffer) instead of O(file + all pools). This is
                // what lets 100MB+ libraries load under the default 128MB memory_limit.
                $offsetTableBase = $poolCursor;                  // absolute offset of the (count+1) u32 offsets
                $poolCursor += ($count + 1) * 4;
                $dataBase = $poolCursor;                         // absolute offset of the raw string bytes
                $totalLen = $this->safeReadU32($offsetTableBase + $count * 4);  // offsets[count] = total region length
                $poolCursor = $dataBase + $totalLen;
                $groupDescs[] = ['ot' => $offsetTableBase, 'db' => $dataBase, 'count' => $count];
            }
            $this->groupPoolDescs[$g] = $groupDescs;
        }
    }

    /**
     * Resolve a single pool string on demand.
     * Reads offsets[idx] / offsets[idx+1] from the offset table and slices the raw
     * bytes via readBytes() (buffered or streamed) — O(1) memory, no eager materialization.
     *
     * @param int $g   group index
     * @param int $f   field index within the group
     * @param int $idx pool entry index
     * @return string
     */
    private function poolString($g, $f, $idx)
    {
        if ($g < 0 || $g >= count($this->groupPoolDescs)) {
            return '';
        }
        if ($f < 0 || $f >= count($this->groupPoolDescs[$g])) {
            return '';
        }
        $desc = $this->groupPoolDescs[$g][$f];
        if ($desc === null) {
            return '';
        }
        if ($idx < 0 || $idx >= $desc['count']) {
            return '';
        }
        $start = $this->safeReadU32($desc['ot'] + $idx * 4);
        $end = $this->safeReadU32($desc['ot'] + ($idx + 1) * 4);
        $length = $end - $start;
        if ($length <= 0) {
            return '';
        }
        return $this->readBytes($desc['db'] + $start, $length);
    }

    private function getV4Child($nodeIdx, $bit)
    {
        if ($nodeIdx >= $this->v4NodeCount) return 0;
        if ($this->v4Node24) {
            $nodeOffset = $this->offV4Nodes + $nodeIdx * 6;
            $offset = $bit === 0 ? $nodeOffset : $nodeOffset + 3;
            $b0 = $this->readByte($offset);
            $b1 = $this->readByte($offset + 1);
            $b2 = $this->readByte($offset + 2);
            $val = $b0 | ($b1 << 8) | ($b2 << 16);
            if ($val & 0x800000) {
                return ($val & 0x7FFFFF) | self::SENTINEL;
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
            $b0 = $this->readByte($offset);
            $b1 = $this->readByte($offset + 1);
            $b2 = $this->readByte($offset + 2);
            $val = $b0 | ($b1 << 8) | ($b2 << 16);
            if ($val & 0x800000) {
                return ($val & 0x7FFFFF) | self::SENTINEL;
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
            $steps++;
            if ($steps >= self::MAX_TRIE_WALK_STEPS) return 0;
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

    private function parseRowSchema()
    {
        $this->rowGeoWidth = 3;
        $this->rowAsnWidth = 3;
        $this->rowUsageWidth = 0;
        if ($this->offRowSchema <= 0) return;
        $sp = $this->offRowSchema;
        // Canonical ROW_SCHEMA layout (matches the QZDB builder / QZDBReader):
        //   byte[sp+0]=fieldCount, byte[sp+1]=stride, bytes[sp+2..3]=reserved,
        //   then fieldCount x 4-byte records: { fieldId, width, offset, flags }.
        //   fieldId: 0=geo, 1=asn, 2=usage.
        $fCount = $this->readByte($sp);
        $stride = $this->readByte($sp + 1);
        if ($fCount < 1 || $fCount > 8) return;
        if ($sp + 4 + $fCount * 4 > $this->fileSize) return;
        if ($stride != $this->ipRowSize) return;

        $geoW = 0; $asnW = 0; $usageW = 0; $total = 0;
        $wpos = $sp + 4;
        $ok = true;
        for ($i = 0; $i < $fCount; $i++) {
            $fid = $this->readByte($wpos);
            $w = $this->readByte($wpos + 1);
            if ($fid === 0) $geoW = $w;
            else if ($fid === 1) $asnW = $w;
            else if ($fid === 2) $usageW = $w;
            $wpos += 4;
            $total += $w;
            if ($w < 1 || $w > 4) $ok = false;
        }
        if ($ok && $total === $this->ipRowSize) {
            $this->rowGeoWidth = $geoW;
            $this->rowAsnWidth = $asnW;
            $this->rowUsageWidth = $usageW;
        }
    }

    private function readIPRow($rowId)
    {
        if ($rowId <= 0 || $rowId >= $this->rowCount) {
            return [0, 0, 0];
        }
        $off = $this->offIPRow + $rowId * $this->ipRowSize;
        $geoId = 0;
        $asnId = 0;
        $usageTypeId = 0;

        if ($this->offRowSchema > 0) {
            $p = $off;
            $geoId = $this->safeReadUintWidth($p, $this->rowGeoWidth);
            $p += $this->rowGeoWidth;
            if ($this->rowAsnWidth > 0) {
                $asnId = $this->safeReadUintWidth($p, $this->rowAsnWidth);
                $p += $this->rowAsnWidth;
            }
            if ($this->rowUsageWidth > 0) {
                $usageTypeId = $this->safeReadUintWidth($p, $this->rowUsageWidth);
            }
        } else {
            $geoId = $this->safeReadU24($off);
            $asnId = $this->safeReadU24($off + 3);
            if ($this->ipRowSize >= 9) {
                $usageTypeId = $this->safeReadU24($off + 6);
            }
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

        $widths = $this->groupFieldWidths[$groupIndex];
        $baseOffsets = $this->groupFieldOffsets[$groupIndex];
        $natives = $this->groupFieldNative[$groupIndex];
        $natTypes = $this->groupFieldNativeType[$groupIndex];

        $values = [];
        for ($i = 0; $i < $fieldCount; $i++) {
            $w = $widths[$i];
            $fo = $entryOffset + $baseOffsets[$i];
            $isNative = $natives && $i < count($natives) && $natives[$i];

            if ($isNative) {
                $t = $natTypes && $i < count($natTypes) ? $natTypes[$i] : 0;
                 if ($t === 1) {
                     // float
                     if ($w === 4) {
                         if ($fo < 0 || $fo + 4 > strlen($this->data)) {
                             throw new QzdbException('Out of bounds reading float32 at offset ' . $fo, self::ERROR_OUT_OF_BOUNDS);
                         }
                         $valNum = unpack('f', $this->data, $fo)[1];
                     } else {
                         if ($fo < 0 || $fo + 8 > strlen($this->data)) {
                             throw new QzdbException('Out of bounds reading float64 at offset ' . $fo, self::ERROR_OUT_OF_BOUNDS);
                         }
                         $valNum = unpack('d', $this->data, $fo)[1];
                     }
                     $val = GeoInfo::formatFloatValue($valNum);
                } else {
                    // int
                    $valNum = $this->safeReadUintWidth($fo, $w);
                    $val = (string)$valNum;
                }
            } else {
                $idx = $this->safeReadUintWidth($fo, $w);
                $val = $this->poolString($groupIndex, $i, $idx);
            }

            $values[] = $val;
        }

        return new GeoInfo($values, $this->fieldNames, $this->floatFieldIndices);
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
        $widths = $this->groupFieldWidths[$groupIndex];
        $baseOffsets = $this->groupFieldOffsets[$groupIndex];
        $natives = $this->groupFieldNative[$groupIndex];
        $natTypes = $this->groupFieldNativeType[$groupIndex];

        $resolved = [];
        foreach ($indices as $i) {
            if ($i < 0 || $i >= $fieldCount) continue;
            $w = $widths[$i];
            $fo = $entryOffset + $baseOffsets[$i];
            $isNative = $natives && $i < count($natives) && $natives[$i];
            if ($isNative) {
                $t = $natTypes && $i < count($natTypes) ? $natTypes[$i] : 0;
                 if ($t === 1) {
                     if ($w === 4) {
                         if ($fo < 0 || $fo + 4 > strlen($this->data)) {
                             throw new QzdbException('Out of bounds reading float32 at offset ' . $fo, self::ERROR_OUT_OF_BOUNDS);
                         }
                         $valNum = unpack('f', $this->data, $fo)[1];
                     } else {
                         if ($fo < 0 || $fo + 8 > strlen($this->data)) {
                             throw new QzdbException('Out of bounds reading float64 at offset ' . $fo, self::ERROR_OUT_OF_BOUNDS);
                         }
                         $valNum = unpack('d', $this->data, $fo)[1];
                     }
                     $resolved[$i] = GeoInfo::formatFloatValue($valNum);
                } else {
                    $resolved[$i] = (string)$this->safeReadUintWidth($fo, $w);
                }
            } else {
                $idx = $this->safeReadUintWidth($fo, $w);
                $resolved[$i] = $this->poolString($groupIndex, $i, $idx);
            }
        }

        $values = [];
        for ($i = 0; $i < $fieldCount; $i++) {
            $values[] = isset($resolved[$i]) ? $resolved[$i] : '';
        }
        return new GeoInfo($values, $this->fieldNames, $this->floatFieldIndices);
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

    private static function crc32bInitTable(): void
    {
        if (self::$crc32bTable !== null) return;
        $table = [];
        for ($i = 0; $i < 256; $i++) {
            $crc = $i;
            for ($j = 0; $j < 8; $j++) {
                $crc = ($crc & 1) ? (0xEDB88320 ^ ($crc >> 1)) : ($crc >> 1);
            }
            $table[$i] = $crc;
        }
        self::$crc32bTable = $table;
    }

    private static function crc32bUpdate(int $crc, string $data): int
    {
        self::crc32bInitTable();
        $table = self::$crc32bTable;
        $len = strlen($data);
        for ($i = 0; $i < $len; $i++) {
            $crc = $table[($crc ^ ord($data[$i])) & 0xFF] ^ ($crc >> 8);
        }
        return $crc;
    }

    private static function crc32bCompute(string $data): int
    {
        return self::crc32bUpdate(0xFFFFFFFF, $data) ^ 0xFFFFFFFF;
    }

    /**
     * CRC32-B over the whole file, treating the stored CRC field at [16,20) as
     * zero (XOR with 0 is identity). When $stream is provided (large-file mode)
     * the bytes are read in chunks so the file never has to be buffered in memory.
     */
    private static function crc32bComputeFile(string $data, $stream = null, int $size = 0): int
    {
        self::crc32bInitTable();
        $table = self::$crc32bTable;
        $crc = 0xFFFFFFFF;

        if ($stream !== null) {
            // Header [0, 16)
            fseek($stream, 0, SEEK_SET);
            $head = fread($stream, 16);
            for ($i = 0; $i < 16 && $i < strlen($head); $i++) {
                $crc = $table[($crc ^ ord($head[$i])) & 0xFF] ^ ($crc >> 8);
            }
            // CRC field [16, 20) counted as zero
            for ($i = 0; $i < 4; $i++) {
                $crc = $table[$crc & 0xFF] ^ ($crc >> 8);
            }
            // Tail [20, size)
            fseek($stream, 20, SEEK_SET);
            $remaining = $size - 20;
            while ($remaining > 0) {
                $chunk = fread($stream, min(65536, $remaining));
                if ($chunk === false || $chunk === '') {
                    break;
                }
                $clen = strlen($chunk);
                for ($i = 0; $i < $clen; $i++) {
                    $crc = $table[($crc ^ ord($chunk[$i])) & 0xFF] ^ ($crc >> 8);
                }
                $remaining -= $clen;
            }
            return $crc ^ 0xFFFFFFFF;
        }

        $len = strlen($data);
        // CRC bytes [0, 16)
        for ($i = 0; $i < 16; $i++) {
            $crc = $table[($crc ^ ord($data[$i])) & 0xFF] ^ ($crc >> 8);
        }
        // CRC field counted as zero (4 zero bytes, XOR with 0 is identity)
        $crc = $table[$crc & 0xFF] ^ ($crc >> 8);
        $crc = $table[$crc & 0xFF] ^ ($crc >> 8);
        $crc = $table[$crc & 0xFF] ^ ($crc >> 8);
        $crc = $table[$crc & 0xFF] ^ ($crc >> 8);
        // CRC bytes [20, end)
        for ($i = 20; $i < $len; $i++) {
            $crc = $table[($crc ^ ord($data[$i])) & 0xFF] ^ ($crc >> 8);
        }
        return $crc ^ 0xFFFFFFFF;
    }

    public function verifyCrc(): bool
    {
        if ($this->fileSize < 20) {
            return false;
        }
        if (16 + 4 > strlen($this->data)) {
            return false;
        }
        $stored = unpack('V', $this->data, 16)[1];
        $computed = self::crc32bComputeFile((string)$this->data, $this->stream, $this->fileSize);
        return $stored === $computed;
    }

    private static $HEX = null;
    private static $crc32bTable = null;

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
