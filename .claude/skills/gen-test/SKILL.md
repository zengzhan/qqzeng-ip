---
name: gen-test
description: Generates cross-language test cases and verification vectors for the qzdb SDK. Creates IP range test vectors, expected results, and language-specific test runners from a single source of truth.
disable-model-invocation: true
---

# Generate Test

Use this skill when adding new test coverage to the qzdb SDK verification suite.

## When to Invoke

- Adding a new IP range test case
- Adding a new field to the SDK and needing test coverage
- Adding a new language implementation that needs test scaffolding
- Fixing a bug and needing a regression test

## Workflow

### Step 1: Define Test Vectors
Add test IP ranges and expected results to the golden vectors file:
- File: `multi-lang/tools/golden_vectors.json`
- Format: `{"ip_range": {"start": "...", "end": "..."}, "expected": {"country": "...", "province": "...", "city": "...", "asn": "...", "isp": "..."}}`
- Include edge cases: private IPs, reserved ranges, boundary IPs, IPv6 addresses

### Step 2: Generate Language-Specific Test Runners
For each language that needs a test runner, create or update:

| Language | Test File | Pattern |
|----------|-----------|---------|
| C | `multi-lang/c/batch_query.c` | Add test case to batch CLI |
| Go | `multi-lang/go/cmd/` | Add test file in Go test format |
| Rust | `multi-lang/rust/src/bin/` | Add test binary or unit test |
| Python | `multi-lang/python/test.py` | Add test function |
| Node.js | `multi-lang/nodejs/test.js` | Add test case |
| Java | `multi-lang/java/src/` | Add JUnit test |
| C# | `multi-lang/netcore/` | Add xUnit test |
| PHP | `multi-lang/php/test.php` | Add test case |

### Step 3: Add Regression Test (for bug fixes)
- File: `multi-lang/test_row_schema_regression.py` or `tools/regenerate_verify.py`
- Add the specific IP and expected result that was previously broken
- Name the test case clearly with the bug description

### Step 4: Run Verification
```bash
cd multi-lang && ./run_all.sh
```

### Step 5: Verify Cross-Language Consistency
```bash
cd multi-lang && python3 cross_lang_verify.py
```

All language implementations must return identical results for the same IP.

## Validation Checklist
- [ ] Golden vectors updated with new test cases
- [ ] Test runners added/updated for affected languages
- [ ] All 4 verification layers pass (L1-L4)
- [ ] Cross-language consistency verified
- [ ] Regression test added for bug fixes
