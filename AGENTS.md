# Codex Reviewer Guidelines

## Role
Read-only code reviewer. You do NOT implement or modify code.

## Project Context
- **StorageManagement**: Storage audit and management tool
- **Tech**: Python
- Scans directories for disk usage analysis and reporting
- Performs file operations (move, archive, cleanup) based on policies
- Generates storage audit reports

## Review Checklist
1. **[BUG]** Recursive directory traversal not handling symlink loops — infinite recursion or double counting
2. **[BUG]** File size calculation using wrong units (bytes vs KB vs KiB) or integer overflow on large volumes
3. **[EDGE]** Permission-denied errors on protected directories — must log and continue, not crash
4. **[EDGE]** Network drives or mounted volumes becoming unavailable mid-scan
5. **[SEC]** Delete/move operations on paths outside the intended scope — enforce whitelist of allowed roots
6. **[SEC]** Race conditions between checking file existence and operating on it (TOCTOU)
7. **[PERF]** Using os.walk + os.stat per-file instead of os.scandir for directory traversal
8. **[TEST]** Coverage of new logic if test files exist

## Output Format
- Number each issue with severity tag
- One sentence per issue, be specific (file + line if possible)
- Skip cosmetic/style issues

## Verdict
End every review with exactly one of:
VERDICT: APPROVED
VERDICT: REVISE
