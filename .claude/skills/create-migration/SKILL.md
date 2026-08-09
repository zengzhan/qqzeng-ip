---
name: create-migration
description: Creates a structured migration for .qzdb format changes or SDK additions across all 8 language implementations. Ensures format spec, all language ports, and verification suite stay in sync.
disable-model-invocation: true
---

# Create Migration

Use this skill when the .qzdb binary format changes, a new language SDK is added, or a cross-cutting change needs to propagate across all 8 language implementations.

## When to Invoke

- The FORMAT.md binary format spec is being updated
- A new language port (C, C#, Go, Java, Node.js, PHP, Python, Rust) is being added
- A new field is added to the IPRow or GeoEntry structure
- The verification suite needs new test cases for a format change

## Workflow

### Step 1: Update FORMAT.md
- Edit `FORMAT.md` in the repo root to reflect the new format
- Update all section references (header layout, IPRow structure, GeoEntry fields, string pools)
- Bump the version indicator if the format change is significant

### Step 2: Update All Language SDKs
For each language implementation under `multi-lang/`, verify and update:

| Language | Key File(s) | What to Check |
|----------|-------------|---------------|
| C | `multi-lang/c/qzdb_reader.c` | mmap layout, field offsets, buffer parsing |
| C# | `multi-lang/netcore/QzdbReader.cs` | BinaryReader offsets, field names |
| Go | `multi-lang/go/qzdb/` | mmap mapping, struct field alignment |
| Java | `multi-lang/java/src/main/java/com/qqzeng/qzdb/QzdbReader.java` | ByteBuffer layout, field indices |
| Node.js | `multi-lang/nodejs/qzdb.js` | Buffer slicing, offset arithmetic |
| PHP | `multi-lang/php/QzdbReader.php` | Unpack format strings, offset calculations |
| Python | `multi-lang/python/qzdb.py` | struct.unpack patterns, mmap offsets |
| Rust | `multi-lang/rust/src/lib.rs` | repr(C) structs, memmap2 mapping |

### Step 3: Update Verification Suite
- Add new test vectors to `multi-lang/tools/golden_vectors.json` if needed
- Update `multi-lang/cross_lang_verify.py` for new field comparisons
- Update `multi-lang/accuracy_analysis.py` for new field validation
- Add regression test cases to `multi-lang/tools/` if the change affects query results

### Step 4: Update SQL Pipeline (if applicable)
- If the change affects IP data or ASN fields, update `sql/ipv6_fusion_pipeline.sql`

### Step 5: Run Verification
```bash
cd multi-lang && ./run_all.sh
```

All 4 layers (L1 smoke, L2 cross-lang, L3 batch, L4 accuracy) must pass.

## Validation Checklist
- [ ] FORMAT.md updated with new field/layout
- [ ] All 8 language SDKs compile without errors
- [ ] Cross-language verification (L2) passes
- [ ] Accuracy analysis (L4) passes
- [ ] New test vectors added if format changed
- [ ] No existing test regressions
