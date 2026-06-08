param(
    [string]$BaseUrl = "http://localhost:5079",
    [string]$WorkspaceId = "default",
    [string]$CollectionId = "test",
    [int]$MaxCandidatesPerTrace = 50,
    [int]$TraceTake = 200,
    [string]$OutputDir = "",
    [switch]$Execute
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RepoRoot {
    $scriptDir = Split-Path -Parent $PSCommandPath
    return (Resolve-Path (Join-Path $scriptDir "..")).Path
}

function Assert-Parameter {
    if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
        throw "BaseUrl is required."
    }
    if ([string]::IsNullOrWhiteSpace($WorkspaceId)) {
        throw "WorkspaceId is required."
    }
    if ([string]::IsNullOrWhiteSpace($CollectionId)) {
        throw "CollectionId is required."
    }
    if ($MaxCandidatesPerTrace -le 0) {
        throw "MaxCandidatesPerTrace must be positive."
    }
    if ($TraceTake -le 0) {
        throw "TraceTake must be positive."
    }
    try {
        [void][Uri]$BaseUrl
    }
    catch {
        throw "BaseUrl must be a valid absolute URI."
    }
}

function Join-Url {
    param(
        [string]$Root,
        [string]$Path
    )
    return $Root.TrimEnd("/") + "/" + $Path.TrimStart("/")
}

function Invoke-Json {
    param(
        [string]$Method,
        [string]$Uri,
        [object]$Body = $null,
        [switch]$Optional
    )

    try {
        if ($null -eq $Body) {
            return Invoke-RestMethod -Method $Method -Uri $Uri -TimeoutSec 30
        }

        $json = $Body | ConvertTo-Json -Depth 20
        return Invoke-RestMethod -Method $Method -Uri $Uri -Body $json -ContentType "application/json" -TimeoutSec 60
    }
    catch {
        if ($Optional) {
            Write-Warning "Optional request failed: $Method $Uri :: $($_.Exception.Message)"
            return $null
        }
        throw
    }
}

function New-RetrievalBody {
    param([object]$Scenario)
    return @{
        operationId = "ranker-shadow-collect-$($Scenario.Id)"
        workspaceId = $WorkspaceId
        collectionId = $CollectionId
        queryText = $Scenario.Query
        topK = 10
        candidateTake = $MaxCandidatesPerTrace
        vectorTopK = 0
        tokenBudget = 4000
        includeKeywordRecall = $true
        includeVectorRecall = $false
        includeRelationExpansion = $true
        includeWorkingMemory = $true
        includeStableMemory = $true
        includeContent = $true
        metadata = @{
            mode = $Scenario.Mode
            sampleScenario = $Scenario.Name
            traceCollection = "ranker-shadow-runbook"
        }
    }
}

function New-PackageBody {
    param([object]$Scenario)
    return @{
        workspaceId = $WorkspaceId
        collectionId = $CollectionId
        queryText = $Scenario.Query
        tokenBudget = 4000
        includeRecent = $true
        metadata = @{
            mode = $Scenario.Mode
            sampleScenario = $Scenario.Name
            traceCollection = "ranker-shadow-runbook"
        }
    }
}

function New-RankerDebugBody {
    param([object]$Scenario)
    return @{
        query = $Scenario.Query
        workspaceId = $WorkspaceId
        collectionId = $CollectionId
        mode = $Scenario.Mode
        candidateIds = @()
        includeLifecycleDetails = $true
        topK = 10
        candidateTake = $MaxCandidatesPerTrace
        tokenBudget = 4000
    }
}

Assert-Parameter

$repoRoot = Resolve-RepoRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "learning\baselines"
}
$resolvedOutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$tracePath = Join-Path $resolvedOutputDir "ranker-shadow-traces.jsonl"
$qualityJsonPath = Join-Path $resolvedOutputDir "ranker-shadow-trace-quality-report.json"
$qualityMarkdownPath = Join-Path $resolvedOutputDir "ranker-shadow-trace-quality-report.md"

$scenarios = @(
    [pscustomobject]@{ Id = "chat-fuzzy-preference"; Name = "Chat fuzzy preference"; Mode = "ChatMode"; Query = "What long-term preference is currently confirmed? Exclude old deprecated preference wording." },
    [pscustomobject]@{ Id = "chat-version-conflict"; Name = "Chat version conflict deprecated noise"; Mode = "ChatMode"; Query = "Which communication preference is current? Exclude deprecated same-keyword noise." },
    [pscustomobject]@{ Id = "novel-character-state"; Name = "Novel character state"; Mode = "NovelMode"; Query = "What is the current character state and recent relationship change? Avoid old character settings." },
    [pscustomobject]@{ Id = "novel-item-state"; Name = "Novel item state old-vs-current"; Mode = "NovelMode"; Query = "What is the current item state and world constraint? Check old-vs-current setting conflicts." },
    [pscustomobject]@{ Id = "project-current-task"; Name = "Project current task deprecated design"; Mode = "ProjectMode"; Query = "What is the current project task and latest design decision? Exclude deprecated design drafts." },
    [pscustomobject]@{ Id = "automation-recovery"; Name = "Automation recovery retry dead-letter"; Mode = "AutomationMode"; Query = "What is the latest failed step, recovery point, retry policy, and dead-letter state?" },
    [pscustomobject]@{ Id = "coding-verification"; Name = "Coding verification deprecated interface"; Mode = "CodingMode"; Query = "What is the current code verification path and interface constraint? Exclude deprecated interface designs." }
)

Write-Host "Ranker shadow trace collection"
Write-Host "BaseUrl    : $BaseUrl"
Write-Host "Workspace  : $WorkspaceId"
Write-Host "Collection : $CollectionId"
Write-Host "OutputDir  : $resolvedOutputDir"
Write-Host "Execute    : $($Execute.IsPresent)"
Write-Host ""

if (-not $Execute) {
    Write-Host "Dry run only. Re-run with -Execute to call ContextCore.Service."
    Write-Host "Planned scenarios:"
    foreach ($scenario in $scenarios) {
        Write-Host "- [$($scenario.Mode)] $($scenario.Name): $($scenario.Query)"
    }
    Write-Host ""
    Write-Host "Planned outputs:"
    Write-Host "- $tracePath"
    Write-Host "- $qualityJsonPath"
    Write-Host "- $qualityMarkdownPath"
    exit 0
}

New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null

$statusUri = Join-Url $BaseUrl "/api/status"
$readyUri = Join-Url $BaseUrl "/api/health/ready"
$retrieveUri = Join-Url $BaseUrl "/api/context/retrieve"
$packageUri = Join-Url $BaseUrl "/api/package/build-detailed"
$debugUri = Join-Url $BaseUrl "/api/retrieval/ranker-shadow/debug"
$traceUri = Join-Url $BaseUrl ("/api/learning/ranker-shadow/traces?workspaceId={0}&collectionId={1}&take={2}&format=jsonl" -f [uri]::EscapeDataString($WorkspaceId), [uri]::EscapeDataString($CollectionId), $TraceTake)

Write-Host "Checking /api/status ..."
[void](Invoke-Json -Method Get -Uri $statusUri -Optional)

Write-Host "Checking /api/health/ready ..."
[void](Invoke-Json -Method Get -Uri $readyUri)

foreach ($scenario in $scenarios) {
    Write-Host "Sampling: $($scenario.Name)"
    [void](Invoke-Json -Method Post -Uri $retrieveUri -Body (New-RetrievalBody $scenario))
    [void](Invoke-Json -Method Post -Uri $packageUri -Body (New-PackageBody $scenario))
    [void](Invoke-Json -Method Post -Uri $debugUri -Body (New-RankerDebugBody $scenario))
}

Write-Host "Exporting ranker shadow traces ..."
$traceText = Invoke-RestMethod -Method Get -Uri $traceUri -TimeoutSec 60
if ($null -eq $traceText) {
    $traceText = ""
}
Set-Content -Path $tracePath -Value $traceText -Encoding UTF8

Write-Host "Running trace quality report ..."
Push-Location $repoRoot
try {
    dotnet run --project src\ContextCore.ControlRoom -- eval ranker-shadow-trace-quality `
        --workspace $WorkspaceId `
        --collection $CollectionId `
        --take $TraceTake `
        --out $qualityJsonPath `
        --md-out $qualityMarkdownPath
    if ($LASTEXITCODE -ne 0) {
        throw "ranker-shadow-trace-quality failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "Generated:"
Write-Host "- $tracePath"
Write-Host "- $qualityJsonPath"
Write-Host "- $qualityMarkdownPath"
Write-Host ""
Write-Host "If TraceCount is 0, verify Learning:RankerShadow:TraceCollectionEnabled=true and restart ContextCore.Service."
