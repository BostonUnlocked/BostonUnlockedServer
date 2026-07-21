param(
    [string]$DestinationRoot,
    [string]$SourceRoot,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Resolve-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        return (Resolve-Path -LiteralPath $Path).Path
    }
    catch {
        return $Path
    }
}

function Sync-Directory {
    param(
        [Parameter(Mandatory = $true)][string]$SourcePath,
        [Parameter(Mandatory = $true)][string]$DestinationPath,
        [Parameter(Mandatory = $true)][string]$Label
    )

    if (-not (Test-Path -LiteralPath $SourcePath)) {
        throw "[sync] missing $Label source: $SourcePath"
    }

    if ($Force -and (Test-Path -LiteralPath $DestinationPath)) {
        Remove-Item -LiteralPath $DestinationPath -Recurse -Force
    }

    if (-not (Test-Path -LiteralPath $DestinationPath)) {
        New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null
    }

    Write-Output "[sync] copying $Label from $SourcePath -> $DestinationPath"
    $null = & robocopy $SourcePath $DestinationPath /MIR /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -ge 8) {
        throw "[sync] robocopy failed for $Label (exit code $LASTEXITCODE)"
    }
}

function Resolve-WorktreeSourceRoot {
    param([Parameter(Mandatory = $true)][string]$Root)

    if ($env:BOSTON_UNLOCKED_LOCAL_SOURCE_ROOT -and (Test-Path -LiteralPath $env:BOSTON_UNLOCKED_LOCAL_SOURCE_ROOT)) {
        return (Resolve-NormalizedPath $env:BOSTON_UNLOCKED_LOCAL_SOURCE_ROOT)
    }

    $normalizedRoot = Resolve-NormalizedPath $Root
    $worktreeGroupRoot = Split-Path -Path $normalizedRoot -Parent
    $workspaceGroup = Split-Path -Path $worktreeGroupRoot -Parent
    if ((Split-Path -Path $workspaceGroup -Leaf) -ne "copilot-worktrees") {
        return $null
    }

    $repoName = Split-Path -Path $worktreeGroupRoot -Leaf
    $sourceParent = Split-Path -Path $workspaceGroup -Parent
    $candidate = Join-Path $sourceParent $repoName
    if (-not (Test-Path -LiteralPath $candidate)) {
        return $null
    }

    return (Resolve-NormalizedPath $candidate)
}

if ([string]::IsNullOrWhiteSpace($DestinationRoot)) {
    $DestinationRoot = Join-Path $PSScriptRoot ".."
}

$DestinationRoot = Resolve-NormalizedPath $DestinationRoot
if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    $SourceRoot = Resolve-WorktreeSourceRoot -Root $DestinationRoot
}
else {
    $SourceRoot = Resolve-NormalizedPath $SourceRoot
}

if ([string]::IsNullOrWhiteSpace($SourceRoot)) {
    Write-Output "[sync] not a worktree checkout; no local resource copy needed"
    return
}

if ([string]::Equals($SourceRoot, $DestinationRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    Write-Output "[sync] source and destination are the same; skipping"
    return
}

$sourceDeps = Join-Path $SourceRoot "server\src\Dependencies"
$sourceStaticData = Join-Path $SourceRoot "server\static-data"
$sourceStreamingAssets = Join-Path $SourceRoot "server\StreamingAssets"

$destDeps = Join-Path $DestinationRoot "server\src\Dependencies"
$destStaticData = Join-Path $DestinationRoot "server\static-data"
$destStreamingAssets = Join-Path $DestinationRoot "server\StreamingAssets"

Sync-Directory -SourcePath $sourceDeps -DestinationPath $destDeps -Label "Dependencies"
Sync-Directory -SourcePath $sourceStaticData -DestinationPath $destStaticData -Label "static-data"
Sync-Directory -SourcePath $sourceStreamingAssets -DestinationPath $destStreamingAssets -Label "StreamingAssets"

Write-Output "[sync] local worktree resources ready"
