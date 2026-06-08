param(
    [string]$A3Report = "eval/eval-report-p15-a3.json",
    [string]$ExtendedReport = "eval/eval-report-p15-extended.json",
    [switch]$SkipBuildTest
)

$ErrorActionPreference = "Stop"

function Invoke-Step {
    param(
        [string]$Name,
        [string[]]$Command
    )

    Write-Host "==> $Name"
    $arguments = @($Command | Select-Object -Skip 1)
    & $Command[0] @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
}

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
        Name = $Name
        TotalSamples = $total
        FailedSamples = $failed
        InvalidSamples = $invalid
        PassRate = [double]$report.PassRate
        MustNotHitViolationCount = [int]$mustNotHitViolationCount
        LifecycleViolationCount = [int]$lifecycleViolationCount
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

if (-not $SkipBuildTest) {
    Invoke-Step "dotnet build" @("dotnet", "build")
    Invoke-Step "dotnet test" @("dotnet", "test")
}

Invoke-Step "A3 baseline eval" @("dotnet", "run", "--project", "src\ContextCore.ControlRoom", "--", "eval", "run", "--out", $A3Report)
Invoke-Step "Extended baseline eval" @("dotnet", "run", "--project", "src\ContextCore.ControlRoom", "--", "eval", "run", "--include-batches", "--out", $ExtendedReport)

Test-Report -Path $A3Report -ExpectedTotal 50 -Name "A3 P15"
Test-Report -Path $ExtendedReport -ExpectedTotal 113 -Name "Extended P15"
Add-BaselineFreezeNote -Path "eval/extended-failure-triage-report.md"

Write-Host "P15 eval regression gate passed."
