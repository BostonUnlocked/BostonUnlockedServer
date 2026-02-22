param(
    [ValidateSet('onefile','onedir','both')]
    [string]$Mode = 'both',

    [string]$PythonExe = "",

    [string]$EntryScript = "tools/patch_embedded_configs.py",

    [string]$Name = "patch_embedded_configs",

    [switch]$Clean
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")) | Select-Object -ExpandProperty Path

function Resolve-RepoPath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) {
        return $path
    }
    if ([System.IO.Path]::IsPathRooted($path)) {
        return $path
    }
    return (Join-Path $repoRoot $path)
}

function Assert-File {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Missing file: $Path"
    }
}

if ([string]::IsNullOrWhiteSpace($PythonExe)) {
    $PythonExe = Join-Path $PSScriptRoot "..\.venv\Scripts\python.exe"
}

$PythonExe = Resolve-RepoPath $PythonExe
$EntryScript = Resolve-RepoPath $EntryScript

Assert-File $PythonExe
Assert-File $EntryScript

if ($Clean) {
    $buildDir = Join-Path $repoRoot "build"
    $clientSetupDir = Join-Path $repoRoot "clientsetup"
    $specFileRoot = Join-Path $repoRoot "$Name.spec"
    $specFileInBuild = Join-Path $buildDir "$Name.spec"

    if (Test-Path -LiteralPath $buildDir) { Remove-Item -Recurse -Force $buildDir }
    if (Test-Path -LiteralPath $clientSetupDir) { Remove-Item -Recurse -Force $clientSetupDir }
    if (Test-Path -LiteralPath $specFileRoot) { Remove-Item -Force $specFileRoot }
    if (Test-Path -LiteralPath $specFileInBuild) { Remove-Item -Force $specFileInBuild }
}

Write-Output "Using Python: $PythonExe"

Push-Location $repoRoot
try {

    # Ensure build dependency exists in the venv
    & $PythonExe -m pip install --upgrade pip | Out-Host
    & $PythonExe -m pip install --upgrade pyinstaller | Out-Host

$commonArgs = @(
    "-m", "PyInstaller",
    "--name", $Name,
    "--noconfirm",
    "--clean",
    "--specpath", "build",
    "--distpath", "clientsetup",
    "--hidden-import", "UnityPy.resources",
    "--collect-submodules", "UnityPy",
    "--collect-data", "UnityPy",
    $EntryScript
)

function Invoke-BuildOnefile {
    Write-Output "Building onefile exe..."
    & $PythonExe @($commonArgs + @("--onefile")) | Out-Host
}

function Invoke-BuildOnedir {
    Write-Output "Building onedir exe..."
    & $PythonExe @($commonArgs + @("--onedir")) | Out-Host
}

switch ($Mode) {
    'onefile' { Invoke-BuildOnefile }
    'onedir'  { Invoke-BuildOnedir }
    'both'    { Invoke-BuildOnefile; Invoke-BuildOnedir }
}
}
finally {
    Pop-Location
}

Write-Output "Build complete. Output in: clientsetup/"
Write-Output "- onefile: clientsetup/$Name.exe"
Write-Output "- onedir:  clientsetup/$Name/ (run clientsetup/$Name/$Name.exe)"
