# qzdb — Claude Code Instructions

## Project Overview

Cross-platform IP geolocation SDK supporting 8 languages: C, C#, Go, Java, Node.js, PHP, Python, Rust. Uses a custom binary format (.qzdb) with Multi-ID PATRICIA Trie + IPRow architecture.

## Key Files

| File | Purpose |
|------|---------|
| `docs/QZDB_FORMAT.md` | Binary format specification (single source of truth for format) |
| `docs/QZDB_SDK_API.md` | SDK API v2.4 specification (single source of truth for SDK design) |
| `docs/QZDB_SYNC_GUIDE.md` | Multi-language SDK synchronization guide |
| `multi-lang/` | SDK implementations for all 8 languages |
| `multi-lang/run_all.sh` | Unified verification orchestrator (L1-L4) |
| `multi-lang/cross_lang_verify.py` | L2: cross-language consistency check |
| `multi-lang/accuracy_analysis.py` | L4: deep accuracy analysis |
| `sql/ipv6_fusion_pipeline.sql` | IPv6 data fusion pipeline |
| `tools/regenerate_verify.py` | Verification regeneration tool |

## Language SDK Locations

- C: `multi-lang/c/qzdb_reader.c` + `qzdb_reader.h`
- C#: `multi-lang/netcore/QzdbReader.cs`
- Go: `multi-lang/go/qzdb/`
- Java: `multi-lang/java/src/main/java/com/qqzeng/qzdb/`
- Node.js: `multi-lang/nodejs/qzdb.js`
- PHP: `multi-lang/php/QzdbReader.php`
- Python: `multi-lang/python/qzdb.py`
- Rust: `multi-lang/rust/src/lib.rs`

## Verification Layers

1. **L1**: Smoke tests per language (`run_all_tests.sh`)
2. **L2**: Cross-language verification (`cross_lang_verify.py`)
3. **L3**: Batch regression with CSV ground truth (`run_batch_test_suite.py`)
4. **L4**: Deep accuracy analysis (`accuracy_analysis.py`)

## Workflow Rules

- **Format changes**: Always update `docs/QZDB_FORMAT.md` first, then update all 8 language ports, then run `cd multi-lang && ./run_all.sh`
- **Bug fixes**: Must include a regression test in the appropriate test file
- **New fields**: Use the `create-migration` skill (`.claude/skills/create-migration/SKILL.md`)
- **New tests**: Use the `gen-test` skill (`.claude/skills/gen-test/SKILL.md`)
- **Cross-language changes**: Run both `code-reviewer` and `security-reviewer` subagents in parallel

## Code Conventions

- C: Follow the existing style in `qzdb_reader.c` (K&R-like, no trailing whitespace)
- Go: Standard `gofmt` formatting
- Rust: Standard `rustfmt` formatting
- Python: PEP 8, use `black` for formatting
- JS/TS: Use `prettier`
- PHP: PS-12, use `phpcs`
- Java: Google Java Style, use `google-java-format`
- C#: .NET naming conventions

## Security Rules

- Never commit `.qzdb` database files (purchased data)
- Never commit `.env` files or secrets
- All mmap operations must validate region size before access
- Error paths must fail closed (return error, not potentially wrong data)
- Buffer operations must be bounds-checked

## Testing

- Run `cd multi-lang && ./run_all.sh` before any commit
- Cross-language verification must pass (L2)
- Accuracy analysis must pass (L4)
- Add regression tests for every bug fix
