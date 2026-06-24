param(
    [string]$BaseUrl = "http://localhost:5079",
    [string]$WorkspaceId = "default",
    [string]$CollectionId = "test",
    [string[]]$Profiles = @("audit-v1", "conflict-v1"),
    [int]$MaxRelationsPerTrace = 50,
    [int]$TraceTake = 200,
    [string]$OutputDir = "",
    [switch]$ListScenarios,
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
    if ($null -eq $Profiles -or $Profiles.Count -eq 0) {
        throw "Profiles is required."
    }
    if ($MaxRelationsPerTrace -le 0) {
        throw "MaxRelationsPerTrace must be positive."
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
        operationId = "graph-shadow-collect-$($Scenario.Id)"
        workspaceId = $WorkspaceId
        collectionId = $CollectionId
        queryText = $Scenario.Query
        topK = 10
        candidateTake = $MaxRelationsPerTrace
        vectorTopK = 0
        tokenBudget = 4000
        includeKeywordRecall = $true
        includeVectorRecall = $false
        includeRelationExpansion = $true
        relationExpansionDepth = 1
        includeWorkingMemory = $true
        includeStableMemory = $true
        includeContent = $true
        metadata = @{
            mode = $Scenario.Mode
            sampleScenario = $Scenario.Name
            traceCollection = "graph-shadow-runbook"
            graphExpansionProfiles = ($Profiles -join ",")
        }
    }
}

function New-QueryBody {
    param([object]$Scenario)
    return @{
        workspaceId = $WorkspaceId
        collectionId = $CollectionId
        queryText = $Scenario.Query
        take = 20
        includeContent = $false
        includeDerived = $true
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
            traceCollection = "graph-shadow-runbook"
            graphExpansionProfiles = ($Profiles -join ",")
        }
    }
}

Assert-Parameter

$repoRoot = Resolve-RepoRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "learning\graph-shadow"
}
$resolvedOutputDir = [System.IO.Path]::GetFullPath($OutputDir)
$tracePath = Join-Path $resolvedOutputDir "graph-expansion-shadow-traces.jsonl"
$qualityJsonPath = Join-Path $resolvedOutputDir "graph-expansion-shadow-trace-quality-report.json"
$qualityMarkdownPath = Join-Path $resolvedOutputDir "graph-expansion-shadow-trace-quality-report.md"
$profileCsv = ($Profiles | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Trim() } | Select-Object -Unique) -join ","

$scenarios = @(
    [pscustomobject]@{ Id = "chat-version-conflict"; Name = "Chat version conflict"; Mode = "ChatMode"; Query = "Which user preference is current? Include version conflict evidence and route deprecated preference outside normal context." },
    [pscustomobject]@{ Id = "chat-deprecated-preference"; Name = "Chat deprecated preference"; Mode = "ChatMode"; Query = "Audit old communication preference and explain whether it was superseded." },
    [pscustomobject]@{ Id = "chat-audit-old-topic"; Name = "Chat audit old topic"; Mode = "ChatMode"; Query = "Show audit evidence for an old topic without selecting deprecated memory as normal context." },
    [pscustomobject]@{ Id = "chat-overwritten-style-rule"; Name = "Chat overwritten style rule"; Mode = "ChatMode"; Query = "Compare overwritten response style rule with the current confirmed style boundary." },
    [pscustomobject]@{ Id = "chat-scope-boundary-old-session"; Name = "Chat scope boundary old session"; Mode = "ChatMode"; Query = "Audit old session scope boundary and keep previous session evidence outside normal context." },
    [pscustomobject]@{ Id = "chat-long-term-preference-conflict"; Name = "Chat long-term preference conflict"; Mode = "ChatMode"; Query = "Check long-term preference conflict between current preference and deprecated preference." },
    [pscustomobject]@{ Id = "project-deprecated-design"; Name = "Project deprecated design"; Mode = "ProjectMode"; Query = "Compare current project design with deprecated design draft and show replacement chain evidence." },
    [pscustomobject]@{ Id = "project-superseded-pool"; Name = "Project superseded pool"; Mode = "ProjectMode"; Query = "Find superseded pool settings and route old pool evidence outside normal context." },
    [pscustomobject]@{ Id = "project-old-storage-choice"; Name = "Project old storage choice"; Mode = "ProjectMode"; Query = "Audit old storage choice versus current filesystem provider decision." },
    [pscustomobject]@{ Id = "project-migration-conflict"; Name = "Project migration conflict"; Mode = "ProjectMode"; Query = "Check migration conflict evidence between old queue design and current service mode policy." },
    [pscustomobject]@{ Id = "project-retired-policy"; Name = "Project retired policy"; Mode = "ProjectMode"; Query = "Review retired package policy and explain why it must stay outside normal context." },
    [pscustomobject]@{ Id = "project-audit-previous-release-plan"; Name = "Project audit previous release plan"; Mode = "ProjectMode"; Query = "Audit previous release plan and keep historical release notes in audit context." },
    [pscustomobject]@{ Id = "novel-old-plot"; Name = "Novel old plot"; Mode = "NovelMode"; Query = "Audit the old plot line and keep historical plot evidence out of normal context." },
    [pscustomobject]@{ Id = "novel-weapon-conflict"; Name = "Novel weapon v1-v2 conflict"; Mode = "NovelMode"; Query = "Compare weapon v1 and v2 conflict evidence and identify the latest item state." },
    [pscustomobject]@{ Id = "novel-world-rule-conflict"; Name = "Novel world rule conflict"; Mode = "NovelMode"; Query = "Check conflicting world rules and route conflict evidence separately." },
    [pscustomobject]@{ Id = "novel-character-state-retcon"; Name = "Novel character state retcon"; Mode = "NovelMode"; Query = "Audit character state retcon and separate old character state from current state." },
    [pscustomobject]@{ Id = "novel-location-rule-superseded"; Name = "Novel location rule superseded"; Mode = "NovelMode"; Query = "Compare superseded location rule with current world constraint." },
    [pscustomobject]@{ Id = "novel-foreshadowing-conflict"; Name = "Novel foreshadowing conflict"; Mode = "NovelMode"; Query = "Check foreshadowing conflict evidence and keep old hint outside normal context." },
    [pscustomobject]@{ Id = "automation-old-backup"; Name = "Automation old backup strategy"; Mode = "AutomationMode"; Query = "Audit old backup strategy and current recovery configuration conflict." },
    [pscustomobject]@{ Id = "automation-conflict-recovery"; Name = "Automation conflict recovery config"; Mode = "AutomationMode"; Query = "Find recovery config conflict evidence and avoid old config in normal context." },
    [pscustomobject]@{ Id = "automation-dead-letter-policy-conflict"; Name = "Automation dead-letter policy conflict"; Mode = "AutomationMode"; Query = "Compare current dead-letter policy with deprecated retry policy evidence." },
    [pscustomobject]@{ Id = "automation-retry-limit-superseded"; Name = "Automation retry limit superseded"; Mode = "AutomationMode"; Query = "Audit superseded retry limit and route old retry setting outside normal context." },
    [pscustomobject]@{ Id = "automation-old-credential-rotation"; Name = "Automation old credential rotation"; Mode = "AutomationMode"; Query = "Review old credential rotation plan against current recovery safeguards." },
    [pscustomobject]@{ Id = "automation-audit-failed-step-history"; Name = "Automation audit failed step history"; Mode = "AutomationMode"; Query = "Audit previous failed step history while keeping historical failure evidence out of normal context." },
    [pscustomobject]@{ Id = "coding-deprecated-interface"; Name = "Coding deprecated interface"; Mode = "CodingMode"; Query = "Compare current interface contract with deprecated interface design." },
    [pscustomobject]@{ Id = "coding-old-timeout"; Name = "Coding old timeout config"; Mode = "CodingMode"; Query = "Audit old timeout config versus current retry timeout setting." },
    [pscustomobject]@{ Id = "coding-obsolete-api-contract"; Name = "Coding obsolete API contract"; Mode = "CodingMode"; Query = "Check obsolete API contract conflict with current endpoint contract." },
    [pscustomobject]@{ Id = "coding-test-policy-conflict"; Name = "Coding test policy conflict"; Mode = "CodingMode"; Query = "Compare current regression gate policy with deprecated test shortcut." },
    [pscustomobject]@{ Id = "coding-build-script-legacy-path"; Name = "Coding build script legacy path"; Mode = "CodingMode"; Query = "Audit legacy build script path and current verification command." },
    [pscustomobject]@{ Id = "coding-deprecated-schema-field"; Name = "Coding deprecated schema field"; Mode = "CodingMode"; Query = "Check deprecated schema field conflict with current DTO contract." }
)

Write-Host "Graph expansion shadow trace collection"
Write-Host "BaseUrl       : $BaseUrl"
Write-Host "Workspace     : $WorkspaceId"
Write-Host "Collection    : $CollectionId"
Write-Host "Profiles      : $profileCsv"
Write-Host "MaxRelations  : $MaxRelationsPerTrace"
Write-Host "OutputDir     : $resolvedOutputDir"
Write-Host "Execute       : $($Execute.IsPresent)"
Write-Host ""

if (-not $Execute) {
    Write-Host "Dry run only. Re-run with -Execute to call ContextCore.Service."
    Write-Host "Required Service config:"
    Write-Host "- Graph:ExpansionShadow:Enabled=true"
    Write-Host "- Graph:ExpansionShadow:TraceCollectionEnabled=true"
    Write-Host "- Graph:ExpansionShadow:Profiles=$profileCsv"
    Write-Host "- Graph:ExpansionShadow:MaxRelationsPerTrace=$MaxRelationsPerTrace"
    Write-Host ""
    Write-Host "Planned scenario count: $($scenarios.Count)"
    if ($ListScenarios) {
        Write-Host "Planned scenarios:"
        foreach ($scenario in $scenarios) {
            Write-Host "- [$($scenario.Mode)] $($scenario.Name): $($scenario.Query)"
        }
    }
    else {
        Write-Host "Use -ListScenarios to print the full scenario list."
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
$queryUri = Join-Url $BaseUrl "/api/context/query"
$packageUri = Join-Url $BaseUrl "/api/package/build-detailed"
$traceUri = Join-Url $BaseUrl ("/api/learning/graph-expansion-shadow/traces?workspaceId={0}&collectionId={1}&take={2}&format=jsonl" -f [uri]::EscapeDataString($WorkspaceId), [uri]::EscapeDataString($CollectionId), $TraceTake)

Write-Host "Checking /api/status ..."
[void](Invoke-Json -Method Get -Uri $statusUri -Optional)

Write-Host "Checking /api/health/ready ..."
[void](Invoke-Json -Method Get -Uri $readyUri)

foreach ($scenario in $scenarios) {
    Write-Host "Sampling: $($scenario.Name)"
    [void](Invoke-Json -Method Post -Uri $retrieveUri -Body (New-RetrievalBody $scenario))
    [void](Invoke-Json -Method Post -Uri $queryUri -Body (New-QueryBody $scenario) -Optional)
    [void](Invoke-Json -Method Post -Uri $packageUri -Body (New-PackageBody $scenario))
}

Write-Host "Exporting graph expansion shadow traces ..."
Add-Type -AssemblyName System.Net.Http
$httpClient = [System.Net.Http.HttpClient]::new()
try {
    $traceText = $httpClient.GetStringAsync($traceUri).GetAwaiter().GetResult()
    if ($null -eq $traceText) {
        $traceText = ""
    }
}
finally {
    $httpClient.Dispose()
}
Set-Content -Path $tracePath -Value $traceText -Encoding UTF8

Write-Host "Running trace quality report ..."
Push-Location $repoRoot
try {
    dotnet run --project src\ContextCore.ControlRoom -- eval graph-expansion-shadow-trace-quality `
        --workspace $WorkspaceId `
        --collection $CollectionId `
        --take $TraceTake `
        --out $qualityJsonPath `
        --md-out $qualityMarkdownPath
    if ($LASTEXITCODE -ne 0) {
        throw "graph-expansion-shadow-trace-quality failed with exit code $LASTEXITCODE"
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
Write-Host "If TraceCount is 0, verify Graph:ExpansionShadow:TraceCollectionEnabled=true and restart ContextCore.Service."
