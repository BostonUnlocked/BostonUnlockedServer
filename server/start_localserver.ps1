param(
    [string]$BindHost = "0.0.0.0",
    [int]$Port = 80,
    [int]$APlayPort = 5055,
    [int]$PhotonPort = 4530,
    [switch]$NoFileLogs,
    [switch]$FixedSeed,
    [switch]$UseSqlite,
    [switch]$UseJson,
    [switch]$MigrateJsonToSqlite,
    [string]$SQLiteDbPath
)

$ErrorActionPreference = "Stop"

Write-Output "[server] launching C# service..."

function Sync-LocalWorktreeResources {
    param([string]$RepoRoot)

    $syncScript = Join-Path $RepoRoot 'tools\sync_local_worktree_resources.ps1'
    if (-not (Test-Path -LiteralPath $syncScript)) {
        return
    }

    Write-Output "[server] syncing local worktree resources before build..."
    & $syncScript -DestinationRoot $RepoRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Local resource sync failed with exit code $LASTEXITCODE"
    }
}

# If a previous run is still active, it will lock output DLLs and cause MSBuild copy failures.
Get-Process -Name "Shadowrun.LocalService.Host" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

function Resolve-MsbuildCommand {
    $candidates = @()

    # Prefer VS/MSBuild Build Tools installations first (supports ToolsVersion=Current).
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        try {
            $installPath = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
            if (-not [string]::IsNullOrWhiteSpace($installPath)) {
                $candidates += (Join-Path $installPath 'MSBuild\Current\Bin\MSBuild.exe')
                $candidates += (Join-Path $installPath 'MSBuild\15.0\Bin\MSBuild.exe')
            }
        }
        catch {
        }
    }

    # Then check PATH for msbuild.exe.
    try {
        $msbuildCmd = Get-Command msbuild.exe -ErrorAction Stop
        if ($msbuildCmd -and -not [string]::IsNullOrWhiteSpace($msbuildCmd.Source)) {
            $candidates += $msbuildCmd.Source
        }
    }
    catch {
    }

    # Fallbacks for older environments.
    $candidates += "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return @{ type = 'exe'; command = $candidate }
        }
    }

    # Final fallback: dotnet msbuild.
    try {
        $dotnetCmd = Get-Command dotnet -ErrorAction Stop
        if ($dotnetCmd -and -not [string]::IsNullOrWhiteSpace($dotnetCmd.Source)) {
            return @{ type = 'dotnet'; command = $dotnetCmd.Source }
        }
    }
    catch {
    }

    $candidates += "$env:WINDIR\Microsoft.NET\Framework\v3.5\MSBuild.exe"
    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            return @{ type = 'exe'; command = $candidate }
        }
    }

    throw "No suitable MSBuild was found. Install Visual Studio Build Tools (MSBuild) or .NET SDK."
}

$msbuildInfo = Resolve-MsbuildCommand
Write-Output "[server] using build tool: $($msbuildInfo.command)"

function Remove-LegacyNugetArtifacts {
    param([string]$ProjectDir)

    if ([string]::IsNullOrWhiteSpace($ProjectDir) -or -not (Test-Path -LiteralPath $ProjectDir)) {
        return
    }

    $objDir = Join-Path $ProjectDir 'obj'
    if (-not (Test-Path -LiteralPath $objDir)) {
        return
    }

    Get-ChildItem -LiteralPath $objDir -Filter '*.nuget.*' -File -ErrorAction SilentlyContinue | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
    }

    $assetsPath = Join-Path $objDir 'project.assets.json'
    if (Test-Path -LiteralPath $assetsPath) {
        Remove-Item -LiteralPath $assetsPath -Force -ErrorAction SilentlyContinue
    }
}

Remove-LegacyNugetArtifacts -ProjectDir (Join-Path $PSScriptRoot 'src\Shadowrun.LocalService.Core')
Remove-LegacyNugetArtifacts -ProjectDir (Join-Path $PSScriptRoot 'src\Shadowrun.LocalService.Host')

$depsDir = Join-Path $PSScriptRoot 'src\Dependencies'
$staticDataDir = Join-Path $PSScriptRoot 'static-data'
$streamingAssetsLevelsDir = Join-Path $PSScriptRoot 'StreamingAssets\levels'

Sync-LocalWorktreeResources -RepoRoot (Join-Path $PSScriptRoot '..')

$requiredDlls = @(
    'APlayCommon.dll',
    'Cliffhanger.Core.Compatibility.dll',
    'Cliffhanger.GameLogic.dll',
    'Cliffhanger.SRO.ServerClientCommons.dll',
    'Ionic.Zip.dll',
    'JsonFx.Json.dll',
    'SRO.Core.Compatibility.dll',
    'protobuf-net.dll',
    'PhotonProxy.Common.dll',
    'PhotonProxy.ChatAndFriends.Client.DTO.dll',
    'PhotonProxy.AccountSystem.Client.DTO.dll',
    'PhotonProxy.Serializer.Client.dll'
)

$requiredStaticData = @(
    'globals.json',
    'metagameplay.json'
)

$missingDlls = @()
foreach ($dll in $requiredDlls) {
    if (-not (Test-Path (Join-Path $depsDir $dll))) {
        $missingDlls += $dll
    }
}

$missingStatic = @()
foreach ($json in $requiredStaticData) {
    if (-not (Test-Path (Join-Path $staticDataDir $json))) {
        $missingStatic += $json
    }
}

if ($missingDlls.Count -gt 0 -or $missingStatic.Count -gt 0 -or -not (Test-Path -LiteralPath $streamingAssetsLevelsDir)) {
    Write-Warning "[server] missing extracted resources."
    if ($missingDlls.Count -gt 0) {
        Write-Warning "[server] missing DLLs in ${depsDir}: $($missingDlls -join ', ')"
    }
    if ($missingStatic.Count -gt 0) {
        Write-Warning "[server] missing static-data in ${staticDataDir}: $($missingStatic -join ', ')"
    }
    if (-not (Test-Path -LiteralPath $streamingAssetsLevelsDir)) {
        Write-Warning "[server] missing StreamingAssets in $streamingAssetsLevelsDir"
    }
    $extractor = Join-Path $PSScriptRoot '..\extractresourcesfrominstallation.ps1'
    throw "Run: $extractor -GameRoot '<path-to-ShadowrunChronicles-install>'"
}

if ($UseSqlite -and $UseJson) {
    Write-Warning "[server] both -UseSqlite and -UseJson were provided; using legacy JSON mode."
}

$effectiveUseSqlite = -not $UseJson

if ($effectiveUseSqlite) {
    $sqliteDlls = @(
        'Mono.Data.Sqlite.dll',
        'sqlite3.dll'
    )

    $missingSqliteDlls = @()
    foreach ($dll in $sqliteDlls) {
        if (-not (Test-Path (Join-Path $depsDir $dll))) {
            $missingSqliteDlls += $dll
        }
    }

    if ($missingSqliteDlls.Count -gt 0) {
        throw "SQLite requested but missing DLLs in ${depsDir}: $($missingSqliteDlls -join ', ')"
    }
}

Push-Location (Join-Path $PSScriptRoot 'src')
try {
    Write-Output "[server] building (net48 host/core with current MSBuild toolchain)..."
    if ($msbuildInfo.type -eq 'dotnet') {
        & $msbuildInfo.command msbuild .\Shadowrun.LocalService.Host\Shadowrun.LocalService.Host.csproj /p:Configuration=Release /v:m
    }
    else {
        & $msbuildInfo.command .\Shadowrun.LocalService.Host\Shadowrun.LocalService.Host.csproj /p:Configuration=Release /v:m
    }
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$exe = Join-Path $PSScriptRoot 'src\Shadowrun.LocalService.Host\bin\Release\Shadowrun.LocalService.Host.exe'
$hostOutDir = Split-Path -Parent $exe
$coreDll = Join-Path $hostOutDir 'Shadowrun.LocalService.Core.dll'
$coreBuildDir = Join-Path $PSScriptRoot 'src\Shadowrun.LocalService.Core\bin\Release'
$depsBuildDir = Join-Path $PSScriptRoot 'src\Dependencies'
if (-not (Test-Path $exe)) {
    throw "Host exe not found after build: $exe"
}

if (Test-Path $coreBuildDir) {
    Get-ChildItem -LiteralPath $coreBuildDir -Filter *.dll | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $hostOutDir $_.Name) -Force
    }
    Get-ChildItem -LiteralPath $coreBuildDir -Filter *.pdb | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $hostOutDir $_.Name) -Force
    }
}

if (Test-Path $depsBuildDir) {
    Get-ChildItem -LiteralPath $depsBuildDir -Filter *.dll | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $hostOutDir $_.Name) -Force
    }
}

if (-not (Test-Path $coreDll)) {
    throw "Core dll not found beside host exe after build: $coreDll"
}

$argsList = @(
    '--host', $BindHost,
    '--port', $Port,
    '--aplay-port', $APlayPort,
    '--photon-port', $PhotonPort
)

if ($NoFileLogs) {
    $argsList += '--no-file-logs'
}

if ($FixedSeed) {
    $argsList += '--fixed-seed'
}

if ($effectiveUseSqlite) {
    $argsList += '--use-sqlite'
}
else {
    $argsList += '--use-json'
}

if ($MigrateJsonToSqlite) {
    $argsList += '--migrate-json-to-sqlite'
}

if (-not [string]::IsNullOrWhiteSpace($SQLiteDbPath)) {
    $argsList += '--sqlite-db-path'
    $argsList += $SQLiteDbPath
}

& $exe @argsList
