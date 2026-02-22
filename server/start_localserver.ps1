param(
    [string]$BindHost = "0.0.0.0",
    [int]$Port = 80,
    [int]$APlayPort = 5055,
    [int]$PhotonPort = 4530
)

$ErrorActionPreference = "Stop"

Write-Output "[server] launching C# service..."

# If a previous run is still active, it will lock output DLLs and cause MSBuild copy failures.
Get-Process -Name "Shadowrun.LocalService.Host" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

$msbuild = "$env:WINDIR\Microsoft.NET\Framework\v3.5\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    throw "MSBuild not found at expected path: $msbuild"
}

$depsDir = Join-Path $PSScriptRoot 'src\Dependencies'
$staticDataDir = Join-Path $PSScriptRoot 'static-data'

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

$streamingAssetsLevelsDir = Join-Path $PSScriptRoot 'StreamingAssets\levels'

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

Push-Location (Join-Path $PSScriptRoot 'src')
try {
    Write-Output "[server] building (net35)..."
    & $msbuild .\Shadowrun.LocalService.Host\Shadowrun.LocalService.Host.csproj /p:Configuration=Release /v:m
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed with exit code $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$exe = Join-Path $PSScriptRoot 'src\Shadowrun.LocalService.Host\bin\Release\Shadowrun.LocalService.Host.exe'
if (-not (Test-Path $exe)) {
    throw "Host exe not found after build: $exe"
}

& $exe --host $BindHost --port $Port --aplay-port $APlayPort --photon-port $PhotonPort
