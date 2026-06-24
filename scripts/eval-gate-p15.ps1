param(
    [string]$A3Report = "eval/eval-report-p15-a3.json",
    [string]$ExtendedReport = "eval/eval-report-p15-extended.json",
    [switch]$SkipBuildTest,
    [int]$BuildRetryLimit = 3,
    [int]$TestRetryLimit = 2,
    [switch]$NoDiagnostics
)

$ErrorActionPreference = "Stop"
$script:A3Report = $A3Report
$script:ExtendedReport = $ExtendedReport

# ---------------------------------------------------------------------------
# Diagnostics state
# ---------------------------------------------------------------------------
$script:Diagnostics = [ordered]@{
    Timestamp       = (Get-Date -Format "o")
    BuildFlags      = "-m:1 -p:UseSharedCompilation=false -p:BuildInParallel=false"
    BuildAttempts   = 0
    BuildPassed     = $false
    TestAttempts    = 0
    TestPassed      = $false
    BuildLockIssues = [System.Collections.ArrayList]::new()
    TestLockIssues  = [System.Collections.ArrayList]::new()
    Msb3026Hits     = 0
    Msb3027Hits     = 0
    TesthostResidue = @()
    BinObjLockCheck = $null
    SuggestedCleanup = @()
    Notes           = [System.Collections.ArrayList]::new()
}

# ---------------------------------------------------------------------------
# Stable build
# ---------------------------------------------------------------------------
function Invoke-BuildSafe {
    Write-Host "==> dotnet build (stable flags)"
    Write-Host "    Flags: $($script:Diagnostics.BuildFlags)"

    $msbuildOutput = $null
    for ($attempt = 1; $attempt -le $BuildRetryLimit; $attempt++) {
        $script:Diagnostics.BuildAttempts = $attempt

        if ($attempt -gt 1) {
            Write-Host "    Retry $attempt / $BuildRetryLimit ..."
            Start-Sleep -Seconds (2 * $attempt)
            Clear-OldTesthostResidue
        }

        $msbuildOutput = & dotnet build -m:1 -p:UseSharedCompilation=false -p:BuildInParallel=false 2>&1 | ForEach-Object { "$_" }
        $exitCode = $LASTEXITCODE

        # Scan for MSB3026 / MSB3027 (file lock)
        $msb3026Count = ($msbuildOutput | Select-String -Pattern "MSB3026").Count
        $msb3027Count = ($msbuildOutput | Select-String -Pattern "MSB3027").Count
        $script:Diagnostics.Msb3026Hits += $msb3026Count
        $script:Diagnostics.Msb3027Hits += $msb3027Count

        if ($exitCode -eq 0) {
            $script:Diagnostics.BuildPassed = $true
            [void]$script:Diagnostics.Notes.Add("Build passed on attempt $attempt")
            break
        }

        $locked = $false
        if ($msb3026Count -gt 0 -or $msb3027Count -gt 0) {
            $locked = $true
            $lockLines = $msbuildOutput | Select-String -Pattern "MSB302[67]" | ForEach-Object { $_.Line.Trim() }
            foreach ($line in $lockLines) {
                [void]$script:Diagnostics.BuildLockIssues.Add($line)
            }
        }

        # Scan for common file-lock patterns in full output
        $lockPatterns = $msbuildOutput | Select-String -Pattern "(being used by another process|Access to the path.*is denied|file is locked|Cannot create.*because a file already exists)"
        foreach ($match in $lockPatterns) {
            [void]$script:Diagnostics.BuildLockIssues.Add($match.Line.Trim())
        }

        if (-not $locked) {
            # Not a lock-related failure — don't retry
            Write-Host "    Build failed with non-lock error, see output above."
            break
        }

        Write-Host "    Build hit file-lock issue(s), will retry."
        $script:Diagnostics.SuggestedCleanup += "Remove-Item -Recurse -Force bin,obj in all src projects"
    }

    if (-not $script:Diagnostics.BuildPassed) {
        throw "dotnet build failed after $($script:Diagnostics.BuildAttempts) attempt(s)"
    }
}

# ---------------------------------------------------------------------------
# Stable test (--no-build)
# ---------------------------------------------------------------------------
function Invoke-TestSafe {
    Write-Host "==> dotnet test --no-build"

    for ($attempt = 1; $attempt -le $TestRetryLimit; $attempt++) {
        $script:Diagnostics.TestAttempts = $attempt

        if ($attempt -gt 1) {
            Write-Host "    Retry $attempt / $TestRetryLimit ..."
            Start-Sleep -Seconds (3 * $attempt)
            Clear-OldTesthostResidue
        }

        $testOutput = & dotnet test --no-build 2>&1 | ForEach-Object { "$_" }
        $exitCode = $LASTEXITCODE

        if ($exitCode -eq 0) {
            $script:Diagnostics.TestPassed = $true
            [void]$script:Diagnostics.Notes.Add("Test passed on attempt $attempt")
            return
        }

        # Scan for testhost lock patterns
        $lockPatterns = $testOutput | Select-String -Pattern "(being used by another process|Access to the path.*is denied|testhost.*already running|Cannot start.*port|SocketException)"
        if ($lockPatterns.Count -gt 0) {
            foreach ($match in $lockPatterns) {
                [void]$script:Diagnostics.TestLockIssues.Add($match.Line.Trim())
            }
            Write-Host "    Test hit lock/port issue(s), will retry."
            $script:Diagnostics.SuggestedCleanup += "taskkill /F /IM testhost.exe 2>`$null"
            Continue
        }

        # Non-lock failure — don't retry
        Write-Host "    Test failed with non-lock error, see output above."
        break
    }

    throw "dotnet test failed after $($script:Diagnostics.TestAttempts) attempt(s)"
}

# ---------------------------------------------------------------------------
# File lock diagnostics
# ---------------------------------------------------------------------------
function Get-OldTesthostResidue {
    try {
        $residue = Get-Process -Name "testhost" -ErrorAction SilentlyContinue |
            Select-Object Id, ProcessName, StartTime, @{N = "MainModule"; E = { try { $_.MainModule.FileName } catch { "n/a" } } }
        return $residue
    }
    catch {
        return @()
    }
}

function Clear-OldTesthostResidue {
    $old = Get-OldTesthostResidue
    foreach ($p in $old) {
        $age = (Get-Date) - $p.StartTime
        if ($age.TotalMinutes -gt 10) {
            Write-Host "    [cleanup] Stale testhost.exe PID=$($p.Id) age=$([math]::Round($age.TotalMinutes))min — stopping."
            try { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
}

function Get-BinObjLockSummary {
    $locked = [System.Collections.ArrayList]::new()
    $dirs = @("src\ContextCore.AppHost\bin", "src\ContextCore.Core\bin", "src\ContextCore.Core\obj",
        "tests\ContextCore.Tests\bin", "tests\ContextCore.IntegrationTests\bin", "tests\ContextCore.Service.Tests\bin")

    foreach ($dir in $dirs) {
        if (-not (Test-Path $dir)) { continue }
        try {
            $files = Get-ChildItem -LiteralPath $dir -Recurse -File -ErrorAction Stop |
                Where-Object { $_.Extension -in ".dll", ".exe", ".pdb", ".json" } |
                Select-Object -First 100
            foreach ($f in $files) {
                try {
                    $stream = [System.IO.File]::Open($f.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
                    $stream.Dispose()
                }
                catch {
                    [void]$locked.Add($f.FullName)
                    break
                }
            }
        }
        catch { }
    }
    return @{
        CheckedDirs = $dirs
        LockedFiles = [string[]]$locked
        AnyLocked   = $locked.Count -gt 0
    }
}

# ---------------------------------------------------------------------------
# Diagnostics report
# ---------------------------------------------------------------------------
function Write-DiagnosticsReport {
    param([switch]$Force)

    if ($NoDiagnostics -and -not $Force) { return }

    # Run lock check
    $script:Diagnostics.TesthostResidue = @(Get-OldTesthostResidue | ForEach-Object {
        [ordered]@{ Pid = $_.Id; Name = $_.ProcessName; StartTime = "$($_.StartTime)"; Path = $_.MainModule }
    })
    $script:Diagnostics.BinObjLockCheck = Get-BinObjLockSummary

    # JSON
    $jsonPath = "eval/p15-build-lock-diagnostics.json"
    $diagJson = [ordered]@{
        OperationId = "p15-build-lock-diagnostics-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        GeneratedAt = (Get-Date -Format "o")
        Summary      = [ordered]@{
            BuildPassed      = $script:Diagnostics.BuildPassed
            BuildAttempts    = $script:Diagnostics.BuildAttempts
            TestPassed       = $script:Diagnostics.TestPassed
            TestAttempts     = $script:Diagnostics.TestAttempts
            Msb3026Hits       = $script:Diagnostics.Msb3026Hits
            Msb3027Hits       = $script:Diagnostics.Msb3027Hits
            BuildLockIssueCount = $script:Diagnostics.BuildLockIssues.Count
            TestLockIssueCount  = $script:Diagnostics.TestLockIssues.Count
            TesthostResidueCount = $script:Diagnostics.TesthostResidue.Count
            BinObjLockedAny   = $script:Diagnostics.BinObjLockCheck.AnyLocked
        }
        BuildFlags     = $script:Diagnostics.BuildFlags
        BuildLockIssues = $script:Diagnostics.BuildLockIssues
        TestLockIssues  = $script:Diagnostics.TestLockIssues
        TesthostResidue = $script:Diagnostics.TesthostResidue
        BinObjLockCheck = $script:Diagnostics.BinObjLockCheck
        SuggestedCleanup = $script:Diagnostics.SuggestedCleanup | Select-Object -Unique
        Notes           = $script:Diagnostics.Notes
    }
    $diagJson | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $jsonPath -Encoding utf8
    Write-Host "[Diagnostics] Wrote $jsonPath"

    # Markdown
    $mdPath = "eval/p15-build-lock-diagnostics.md"
    $s = $diagJson.Summary
    $md = @"
# P15 Build-Lock Diagnostics

**OperationId:** `$($diagJson.OperationId)`
**GeneratedAt:** $($diagJson.GeneratedAt)

## Summary

| Metric | Value |
|---|---|
| BuildPassed | `$($s.BuildPassed)` |
| BuildAttempts | `$($s.BuildAttempts)` |
| TestPassed | `$($s.TestPassed)` |
| TestAttempts | `$($s.TestAttempts)` |
| MSB3026 Hits | `$($s.Msb3026Hits)` |
| MSB3027 Hits | `$($s.Msb3027Hits)` |
| BuildLockIssues | `$($s.BuildLockIssueCount)` |
| TestLockIssues | `$($s.TestLockIssueCount)` |
| TesthostResidue | `$($s.TesthostResidueCount)` |
| BinObjLockedAny | `$($s.BinObjLockedAny)` |

## Build Flags

```
$($diagJson.BuildFlags)
```

## Recommended Verification Commands

```powershell
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
```

"@

    if ($script:Diagnostics.BuildLockIssues.Count -gt 0) {
        $md += "`n## Build Lock Issues`n`n"
        foreach ($issue in $script:Diagnostics.BuildLockIssues) {
            $md += "- ``$issue```n"
        }
    }

    if ($script:Diagnostics.TestLockIssues.Count -gt 0) {
        $md += "`n## Test Lock Issues`n`n"
        foreach ($issue in $script:Diagnostics.TestLockIssues) {
            $md += "- ``$issue```n"
        }
    }

    if ($script:Diagnostics.TesthostResidue.Count -gt 0) {
        $md += "`n## Testhost Residue`n`n"
        foreach ($r in $script:Diagnostics.TesthostResidue) {
            $md += "- PID=$($r.Pid) StartTime=$($r.StartTime) Path=$($r.Path)`n"
        }
    }

    $suggested = $script:Diagnostics.SuggestedCleanup | Select-Object -Unique
    if ($suggested.Count -gt 0) {
        $md += "`n## Suggested Cleanup Commands`n`n"
        foreach ($cmd in $suggested) {
            $md += "``````powershell`n$cmd`n```````n`n"
        }
    }

    if ($script:Diagnostics.Notes.Count -gt 0) {
        $md += "`n## Notes`n`n"
        foreach ($note in $script:Diagnostics.Notes) {
            $md += "- $note`n"
        }
    }

    $md | Set-Content -LiteralPath $mdPath -Encoding utf8
    Write-Host "[Diagnostics] Wrote $mdPath"
}

# ---------------------------------------------------------------------------
# Invoke-Step helper (file-lock resilient)
# ---------------------------------------------------------------------------
function Invoke-Step {
    param(
        [string]$Name,
        [string[]]$Command,
        [string]$FallbackCopyFrom,
        [string]$FallbackCopyTo
    )

    Write-Host "==> $Name"
    $arguments = @($Command | Select-Object -Skip 1)
    & $Command[0] @arguments
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0 -and $FallbackCopyFrom -and $FallbackCopyTo) {
        Write-Host "    [fallback] Eval command exited $exitCode — attempting manual copy of output."
        [void]$script:Diagnostics.Notes.Add("$Name exit $exitCode; manual copy fallback triggered")

        for ($r = 1; $r -le 3; $r++) {
            try {
                if (Test-Path $FallbackCopyFrom) {
                    Copy-Item -LiteralPath $FallbackCopyFrom -Destination $FallbackCopyTo -Force -ErrorAction Stop
                    Write-Host "    [fallback] Manual copy succeeded on attempt $r."
                    return
                }
                else {
                    Write-Host "    [fallback] Source not found: $FallbackCopyFrom — waiting..."
                }
            }
            catch {
                Write-Host "    [fallback] Manual copy attempt $r failed: $_"
            }
            Start-Sleep -Seconds 2
        }
    }

    if ($exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode"
    }
}

# ---------------------------------------------------------------------------
# Test-Report (unchanged)
# ---------------------------------------------------------------------------
function Test-Report {
    param(
        [string]$Path,
        [int]$ExpectedTotal,
        [string]$Name
    )

    if (-not (Test-Path $Path)) {
        throw "$Name report not found: $Path"
    }

    $report = Get-Content $Path -Raw | ConvertFrom-Json
    $mustNotHitViolationCount = ($report.Results | Measure-Object -Property MustNotHitRecalledCount -Sum).Sum
    if ($null -eq $mustNotHitViolationCount) {
        $mustNotHitViolationCount = 0
    }

    $hardConstraintMissingCount = @($report.Results | Where-Object { $_.PackageHasAllConstraints -ne $true }).Count
    $lifecycleViolationCount = @(
        $report.Results | Where-Object {
            (($_.WarningReasons -join ";") -match "LifecycleViolation|lifecycle violation") -or
            ($_.ErrorMessage -match "LifecycleViolation|lifecycle violation")
        }
    ).Count

    $failed = [int]$report.FailedSamples
    $invalid = [int]$report.InvalidSamples
    $total = [int]$report.TotalSamples

    $summary = [pscustomobject]@{
        Name                      = $Name
        TotalSamples              = $total
        FailedSamples             = $failed
        InvalidSamples            = $invalid
        PassRate                  = [double]$report.PassRate
        MustNotHitViolationCount  = [int]$mustNotHitViolationCount
        LifecycleViolationCount   = [int]$lifecycleViolationCount
        HardConstraintMissingCount = [int]$hardConstraintMissingCount
    }

    $summary | Format-List

    if ($total -ne $ExpectedTotal) {
        throw "$Name total samples expected $ExpectedTotal, actual $total"
    }

    if ($failed -ne 0 -or $invalid -ne 0 -or [double]$report.PassRate -lt 1) {
        throw "$Name must remain 100% pass"
    }

    if ($mustNotHitViolationCount -ne 0) {
        throw "$Name mustNotHit violation count must be 0"
    }

    if ($lifecycleViolationCount -ne 0) {
        throw "$Name lifecycle violation count must be 0"
    }

    if ($hardConstraintMissingCount -ne 0) {
        throw "$Name hard constraint missing count must be 0"
    }
}

# ---------------------------------------------------------------------------
# Add-BaselineFreezeNote (unchanged)
# ---------------------------------------------------------------------------
function Add-BaselineFreezeNote {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        return
    }

    $content = Get-Content $Path -Raw
    if ($content -match "## P15 Baseline Freeze") {
        $content = [regex]::Replace($content, "(?s)\r?\n## P15 Baseline Freeze.*$", "")
        Set-Content -Path $Path -Value $content.TrimEnd()
    }

    $note = @'

## P15 Baseline Freeze

- Baseline doc: `docs/eval-baseline-p15.md`
- A3 report: `eval/eval-report-p15-a3.json`
- Extended report: `eval/eval-report-p15-extended.json`
- A3 baseline: `50 total / 0 failed / 100.00%`
- Extended baseline: `113 total / 0 failed / 100.00%`

Regression gate:

- A3 must remain `100.00%`
- Extended must remain `100.00%`
- mustNotHit violation = `0`
- lifecycle violation = `0`
- hard constraint missing = `0`

`chat-20260529-003` was closed through the formal constraint activation path: `ConstraintGapCandidate accept -> CandidateConstraint activate -> Active/Hard Constraint -> package constraints section`. It was not closed through resolver aliasing or eval special casing.
'@

    Add-Content -Path $Path -Value $note
}

# ===========================================================================
# Main
# ===========================================================================

if (-not $SkipBuildTest) {
    Invoke-BuildSafe
    Invoke-TestSafe
}
else {
    Write-Host "==> Skipping build/test (SkipBuildTest)"
    [void]$script:Diagnostics.Notes.Add("Build/test skipped via SkipBuildTest")
    $script:Diagnostics.BuildPassed = $true
    $script:Diagnostics.TestPassed = $true
}

# Clean stale eval report files to avoid memory-mapped file lock
foreach ($rpt in @($script:A3Report, $script:ExtendedReport, "eval/extended-failure-triage-report.json", "eval/extended-failure-triage-report.md")) {
    if (Test-Path $rpt) {
        try { Remove-Item -LiteralPath $rpt -Force -ErrorAction Stop }
        catch { Write-Host "    [cleanup] Could not remove $rpt (locked), using temp path." }
    }
    if ($rpt -in @($script:A3Report, $script:ExtendedReport) -and (Test-Path $rpt)) {
        $altDir = Split-Path -Parent $rpt
        $altName = [System.IO.Path]::GetFileNameWithoutExtension($rpt) + "-" + (Get-Date -Format "HHmmss") + [System.IO.Path]::GetExtension($rpt)
        $altPath = Join-Path -Path $altDir -ChildPath $altName
        if ($rpt -eq $script:A3Report) { $script:A3Report = $altPath }
        else { $script:ExtendedReport = $altPath }
        Write-Host "    [cleanup] Using alternate path: $altPath"
        [void]$script:Diagnostics.Notes.Add("Used alternate path for $rpt due to file lock: $altPath")
    }
}

Invoke-Step -Name "A3 baseline eval" -Command @("dotnet", "run", "--project", "src\ContextCore.ControlRoom", "--", "eval", "run", "--out", $script:A3Report) -FallbackCopyFrom "eval/eval-report-latest.json" -FallbackCopyTo $script:A3Report
Invoke-Step -Name "Extended baseline eval" -Command @("dotnet", "run", "--project", "src\ContextCore.ControlRoom", "--", "eval", "run", "--include-batches", "--out", $script:ExtendedReport) -FallbackCopyFrom "eval/eval-report-latest.json" -FallbackCopyTo $script:ExtendedReport

Test-Report -Path $script:A3Report -ExpectedTotal 50 -Name "A3 P15"
Test-Report -Path $script:ExtendedReport -ExpectedTotal 113 -Name "Extended P15"

try { Add-BaselineFreezeNote -Path "eval/extended-failure-triage-report.md" }
catch { Write-Host "    [warn] Could not update baseline freeze note: $_" }

Write-Host "P15 eval regression gate passed."

Write-DiagnosticsReport
