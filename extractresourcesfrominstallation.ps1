param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$GameRoot,

    [switch]$SkipDependencies,
    [switch]$SkipStaticData,
    [switch]$SkipStreamingAssets,

    [switch]$InstallPythonDeps,

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

function Assert-Path {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not (Test-Path -LiteralPath $Path)) {
        throw $Message
    }
}

function New-DirectoryIfMissing {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        if ($PSCmdlet.ShouldProcess($Path, "Create directory")) {
            New-Item -ItemType Directory -Path $Path -Force | Out-Null
        }
    }
}

function Get-FirstFilePath {
    param(
        [Parameter(Mandatory = $true)][string]$FileName,
        [Parameter(Mandatory = $true)][string[]]$SearchRoots
    )

    foreach ($root in $SearchRoots) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        if (-not (Test-Path -LiteralPath $root)) { continue }

        $candidate = Join-Path $root $FileName
        if (Test-Path -LiteralPath $candidate) {
            return (Resolve-NormalizedPath $candidate)
        }
    }

    foreach ($root in $SearchRoots) {
        if ([string]::IsNullOrWhiteSpace($root)) { continue }
        if (-not (Test-Path -LiteralPath $root)) { continue }

        try {
            $found = Get-ChildItem -LiteralPath $root -Recurse -File -Filter $FileName -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($found) {
                return (Resolve-NormalizedPath $found.FullName)
            }
        }
        catch {
            Write-Verbose "[extract] ignored search error under '$root': $($_.Exception.Message)"
        }
    }

    return $null
}

function Get-PythonCommand {
    $py = Get-Command -Name py -ErrorAction SilentlyContinue
    if ($py) {
        return @($py.Path, "-3")
    }

    $python = Get-Command -Name python -ErrorAction SilentlyContinue
    if ($python) {
        return @($python.Path)
    }

    return $null
}

function Test-PythonModule {
    param(
        [Parameter(Mandatory = $true)][string]$PythonExe,
        [Parameter()][string[]]$PythonArgs = @(),
        [Parameter(Mandatory = $true)][string]$ModuleName
    )
    try {
        & $PythonExe @PythonArgs -c "import $ModuleName" | Out-Null
        return ($LASTEXITCODE -eq 0)
    }
    catch {
        return $false
    }
}

$repoRoot = Resolve-NormalizedPath $PSScriptRoot
$localServiceRoot = Join-Path $repoRoot "server"

$GameRoot = Resolve-NormalizedPath $GameRoot

$assetsPath = Join-Path (Join-Path $GameRoot "Shadowrun_Data") "resources.assets"
$managedDir = Join-Path (Join-Path $GameRoot "Shadowrun_Data") "Managed"
$pluginsDir = Join-Path (Join-Path $GameRoot "Shadowrun_Data") "Plugins"
$shadowrunDataDir = Join-Path $GameRoot "Shadowrun_Data"

Assert-Path -Path $assetsPath -Message "resources.assets not found. Expected: $assetsPath"

Write-Output "[extract] repoRoot=$repoRoot"
Write-Output "[extract] gameRoot=$GameRoot"

if (-not $SkipDependencies) {
    $depsOutDir = Join-Path $localServiceRoot "src\\Dependencies"
    New-DirectoryIfMissing $depsOutDir

    $dllNames = @(
        "APlayCommon.dll",
        "Cliffhanger.ChatAndFriends.Interfaces.dll",
        "Cliffhanger.Core.Compatibility.dll",
        "Cliffhanger.GameLogic.dll",
        "Cliffhanger.SRO.ServerClientCommons.dll",
        "Ionic.Zip.dll",
        "JsonFx.Json.dll",
        "PhotonProxy.AccountSystem.Client.DTO.dll",
        "PhotonProxy.ChatAndFriends.Client.DTO.dll",
        "PhotonProxy.Common.dll",
        "PhotonProxy.Serializer.Client.dll",
        "protobuf-net.dll",
        "SRO.Core.Compatibility.dll"
    )

    $searchRoots = @($managedDir, $pluginsDir, $shadowrunDataDir, $GameRoot)

    Write-Output "[extract] copying Dependencies (*.dll)..."
    foreach ($dll in $dllNames) {
        $src = Get-FirstFilePath -FileName $dll -SearchRoots $searchRoots
        if (-not $src) {
            $msg = "Missing required DLL '$dll' in install. Looked in: $($searchRoots -join '; ')"
            if ($Force) {
                Write-Warning "[extract] $msg"
                continue
            }
            throw $msg
        }
        Copy-Item -LiteralPath $src -Destination (Join-Path $depsOutDir $dll) -Force
    }
}

if (-not $SkipStreamingAssets) {
    $streamingAssetsInDir = Join-Path $shadowrunDataDir "StreamingAssets"
    if (-not (Test-Path -LiteralPath $streamingAssetsInDir)) {
        $msg = "StreamingAssets dir not found. Expected: $streamingAssetsInDir"
        if ($Force) {
            Write-Warning "[extract] $msg"
        }
        else {
            throw $msg
        }
    }
    else {
        $streamingAssetsOutDir = Join-Path $localServiceRoot "StreamingAssets"

        if ($Force -and (Test-Path -LiteralPath $streamingAssetsOutDir)) {
            Remove-Item -LiteralPath $streamingAssetsOutDir -Recurse -Force
        }

        New-DirectoryIfMissing $streamingAssetsOutDir

        Write-Output "[extract] copying StreamingAssets (full folder)..."
        # robocopy exit codes: 0-7 are success (files copied/extra/etc). >=8 indicates failure.
        $null = & robocopy $streamingAssetsInDir $streamingAssetsOutDir /E /NFL /NDL /NJH /NJS /NP
        if ($LASTEXITCODE -ge 8) {
            throw "robocopy failed copying StreamingAssets (exit code $LASTEXITCODE)"
        }

        $gitkeep = Join-Path $streamingAssetsOutDir ".gitkeep"
        if (-not (Test-Path -LiteralPath $gitkeep)) {
            Set-Content -LiteralPath $gitkeep -Value "" -NoNewline
        }
    }
}

if (-not $SkipStaticData) {
    $staticOutDir = Join-Path $localServiceRoot "static-data"
    New-DirectoryIfMissing $staticOutDir

    $extractScript = Join-Path $repoRoot "tools\\extract_static_gamelogic.py"
    Assert-Path -Path $extractScript -Message "Extractor script not found: $extractScript"

    $pythonCmd = Get-PythonCommand
    if (-not $pythonCmd) {
        throw "Python not found (tried 'py -3' and 'python'). Install Python 3, then run: py -3 -m pip install UnityPy"
    }

    $pythonExe = $pythonCmd[0]
    $pythonArgs = @()
    if ($pythonCmd.Count -gt 1) {
        $pythonArgs = @($pythonCmd[1..($pythonCmd.Count - 1)])
    }

    if (-not (Test-PythonModule -PythonExe $pythonExe -PythonArgs $pythonArgs -ModuleName "UnityPy")) {
        if ($InstallPythonDeps) {
            Write-Output "[extract] installing Python deps (UnityPy)..."
            & $pythonExe @pythonArgs -m pip install --upgrade pip | Out-Host
            & $pythonExe @pythonArgs -m pip install --upgrade UnityPy | Out-Host
        }

        if (-not (Test-PythonModule -PythonExe $pythonExe -PythonArgs $pythonArgs -ModuleName "UnityPy")) {
            throw "Python module 'UnityPy' is missing. Install it with: $pythonExe $($pythonArgs -join ' ') -m pip install UnityPy (or re-run with -InstallPythonDeps)"
        }
    }

    $names = @(
        "agent.json",
        "clientBaseConfig.json",
        "credits.json",
        "dialogMessages.json",
        "globals.json",
        "ids.json",
        "index.json",
        "LauncherConfig.json",
        "map.json",
        "metagameplay.json",
        "presentation.json",
        "serverData.json",
        "weapons.json"
    )

    Write-Output "[extract] extracting static-data from resources.assets via UnityPy..."
    & $pythonExe @pythonArgs $extractScript --assets $assetsPath --out $staticOutDir --names ($names -join ",")
    if ($LASTEXITCODE -ne 0) {
        throw "Static-data extraction failed with exit code $LASTEXITCODE"
    }
}

Write-Output "[extract] done"
