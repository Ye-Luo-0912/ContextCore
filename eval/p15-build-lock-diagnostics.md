# P15 Build-Lock Diagnostics

**OperationId:** $(System.Collections.Specialized.OrderedDictionary.OperationId)
**GeneratedAt:** 2026-06-27T03:24:23.9096243+08:00

## Summary

| Metric | Value |
|---|---|
| BuildPassed | $(System.Collections.Specialized.OrderedDictionary.BuildPassed) |
| BuildAttempts | $(System.Collections.Specialized.OrderedDictionary.BuildAttempts) |
| TestPassed | $(System.Collections.Specialized.OrderedDictionary.TestPassed) |
| TestAttempts | $(System.Collections.Specialized.OrderedDictionary.TestAttempts) |
| MSB3026 Hits | $(System.Collections.Specialized.OrderedDictionary.Msb3026Hits) |
| MSB3027 Hits | $(System.Collections.Specialized.OrderedDictionary.Msb3027Hits) |
| BuildLockIssues | $(System.Collections.Specialized.OrderedDictionary.BuildLockIssueCount) |
| TestLockIssues | $(System.Collections.Specialized.OrderedDictionary.TestLockIssueCount) |
| TesthostResidue | $(System.Collections.Specialized.OrderedDictionary.TesthostResidueCount) |
| BinObjLockedAny | $(System.Collections.Specialized.OrderedDictionary.BinObjLockedAny) |

## Build Flags

`
-m:1 -p:UseSharedCompilation=false -p:BuildInParallel=false
`

## Recommended Verification Commands

`powershell
# Full stable build + test + eval
scripts/eval-gate-p15.ps1

# Skip build/test (already clean)
scripts/eval-gate-p15.ps1 -SkipBuildTest

# Standalone stable build
dotnet build -m:1 -p:UseSharedCompilation=false -p:BuildInParallel=false

# Standalone stable test
dotnet test --no-build

# If file-lock persists, try:
#   1. Close Visual Studio / VS Code / JetBrains (they hold bin/obj handles)
#   2. taskkill /F /IM testhost.exe
#   3. Remove-Item -Recurse -Force src\*\bin, src\*\obj, tests\*\bin, tests\*\obj
#   4. dotnet restore
#   5. dotnet build -m:1 -p:UseSharedCompilation=false -p:BuildInParallel=false
`

## Notes

- Build passed on attempt 1
- Test passed on attempt 1

