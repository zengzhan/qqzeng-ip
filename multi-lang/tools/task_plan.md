# Task Plan: Cross-Language IP Database Verification

## Goal
Verify that all 8 language SDKs (C, Go, Rust, Node.js, Python, PHP, Java, C#) produce **identical results** for every IP range in the CSV source data — testing start_ip, end_ip, and random_ip within each range. This is customer delivery acceptance criteria.

## Databases Under Test
| Database | CSV Rows | Fields | Scope |
|----------|----------|--------|-------|
| std_china | 182,619 | 9 | China region, basic fields |
| max_china | 448,297 | 29 | China region, full fields |
| max_global | 2,779,330 | 29 | Global, full fields |

## Languages Under Test
- Python (reference implementation)
- C
- Go
- Rust
- Node.js
- PHP
- Java
- C# (.NET Core)

## Phases
- [ ] Phase 1: Create test case generator (Python — reads range CSV, generates start/end/random IPs)
- [ ] Phase 2: Create batch runner for each language
  - [ ] Python runner
  - [ ] C runner
  - [ ] Go runner
  - [ ] Rust runner
  - [ ] Node.js runner
  - [ ] PHP runner
  - [ ] Java runner
  - [ ] C# runner
- [ ] Phase 3: Create comparison/reporting script
- [ ] Phase 4: Run tests across all databases
- [ ] Phase 5: Report results and fix any discrepancies

## Key Decisions
- **Test method**: Generate shared test IP file → each language batch-queries → diff outputs
- **Expected = Python reference**: Python is the reference implementation; all others must match it
- **Sampling**: Small DBs (std_china, max_china) → exhaustive; Large DBs (max_global) → every Nth row
- **Output format**: Pipe-separated geo string matching `geo_to_str()` format
- **Random IP**: Use deterministic seeded RNG for reproducibility

## Errors Encountered
- (none yet)

## Status
**Currently in Phase 1** - Creating test infrastructure
