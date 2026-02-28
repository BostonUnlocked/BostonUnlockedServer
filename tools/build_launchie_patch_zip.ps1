param(
    [Parameter(Mandatory = $true)]
    [string]$SourceRoot,

    [Parameter(Mandatory = $true)]
    [string]$TargetRoot,

    [string]$PatchRoot = "server/config/patches",

    [string]$FromVersion = "",

    [string]$ToVersion = "",

    [string[]]$ExcludeRegex = @(
        '(^|/)output_log\.txt$',
        '(^|/)[^/]+\.bak$',
        '(^|/)steam_api\.dll$'
    ),

    [switch]$WriteVersions,

    [switch]$SyncRuntimeResources,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Resolve-RepoPath([string]$path) {
    if ([string]::IsNullOrWhiteSpace($path)) {
        return $path
    }
    if ([System.IO.Path]::IsPathRooted($path)) {
        return (Resolve-Path -LiteralPath $path).Path
    }
    return (Resolve-Path -LiteralPath (Join-Path $repoRoot $path)).Path
}

function Read-VersionOrThrow([string]$root, [string]$fallback) {
    if (-not [string]::IsNullOrWhiteSpace($fallback)) {
        return $fallback.Trim()
    }

    $versionPath = Join-Path $root 'version.txt'
    if (-not (Test-Path -LiteralPath $versionPath)) {
        throw "Missing version.txt: $versionPath"
    }

    $value = (Get-Content -LiteralPath $versionPath -TotalCount 1).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Empty version value in: $versionPath"
    }

    return $value
}

function Normalize-RelPath([string]$root, [string]$fullPath) {
    $relative = $fullPath.Substring($root.Length).TrimStart([char]'\', [char]'/')
    return ($relative -replace '\\', '/')
}

function Should-Exclude([string]$relativePath, [string[]]$patterns) {
    if ($relativePath.StartsWith('__patch/', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    foreach ($pattern in $patterns) {
        if ($relativePath -match $pattern) {
            return $true
        }
    }

    return $false
}

function Set-ZipUtf8Flags([string]$zipPath) {
    $file = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        [int64]$len = $file.Length
        [int]$tailSize = 131072
        if ($len -lt $tailSize) {
            $tailSize = [int]$len
        }

        $file.Seek($len - $tailSize, [System.IO.SeekOrigin]::Begin) | Out-Null
        $tail = New-Object byte[] $tailSize
        [void]$file.Read($tail, 0, $tailSize)

        $eocd = -1
        for ($i = $tailSize - 22; $i -ge 0; $i--) {
            if ($tail[$i] -eq 0x50 -and $tail[$i + 1] -eq 0x4b -and $tail[$i + 2] -eq 0x05 -and $tail[$i + 3] -eq 0x06) {
                $eocd = $i
                break
            }
        }

        if ($eocd -lt 0) {
            throw "EOCD not found in zip: $zipPath"
        }

        $entryCount = [BitConverter]::ToUInt16($tail, $eocd + 10)
        $centralOffset = [BitConverter]::ToUInt32($tail, $eocd + 16)

        [int64]$position = $centralOffset
        for ($n = 0; $n -lt $entryCount; $n++) {
            $file.Seek($position, [System.IO.SeekOrigin]::Begin) | Out-Null
            $header = New-Object byte[] 46
            [void]$file.Read($header, 0, 46)

            if ($header[0] -ne 0x50 -or $header[1] -ne 0x4b -or $header[2] -ne 0x01 -or $header[3] -ne 0x02) {
                throw "Bad central directory header at offset $position"
            }

            $centralFlag = [BitConverter]::ToUInt16($header, 8)
            $newCentralFlag = ($centralFlag -bor 0x0800)
            if ($newCentralFlag -ne $centralFlag) {
                $bytes = [BitConverter]::GetBytes([UInt16]$newCentralFlag)
                $file.Seek($position + 8, [System.IO.SeekOrigin]::Begin) | Out-Null
                $file.Write($bytes, 0, 2)
            }

            $nameLen = [BitConverter]::ToUInt16($header, 28)
            $extraLen = [BitConverter]::ToUInt16($header, 30)
            $commentLen = [BitConverter]::ToUInt16($header, 32)
            $localOffset = [BitConverter]::ToUInt32($header, 42)

            $file.Seek([int64]$localOffset + 6, [System.IO.SeekOrigin]::Begin) | Out-Null
            $localFlagBytes = New-Object byte[] 2
            [void]$file.Read($localFlagBytes, 0, 2)
            $localFlag = [BitConverter]::ToUInt16($localFlagBytes, 0)
            $newLocalFlag = ($localFlag -bor 0x0800)
            if ($newLocalFlag -ne $localFlag) {
                $bytes = [BitConverter]::GetBytes([UInt16]$newLocalFlag)
                $file.Seek([int64]$localOffset + 6, [System.IO.SeekOrigin]::Begin) | Out-Null
                $file.Write($bytes, 0, 2)
            }

            $position += (46 + $nameLen + $extraLen + $commentLen)
        }
    }
    finally {
        $file.Dispose()
    }
}

$sourceRoot = Resolve-RepoPath $SourceRoot
$targetRoot = Resolve-RepoPath $TargetRoot
if (-not (Test-Path -LiteralPath $sourceRoot)) { throw "Source root not found: $sourceRoot" }
if (-not (Test-Path -LiteralPath $targetRoot)) { throw "Target root not found: $targetRoot" }

$fromVersion = Read-VersionOrThrow -root $sourceRoot -fallback $FromVersion
$toVersion = Read-VersionOrThrow -root $targetRoot -fallback $ToVersion
$pair = "${fromVersion}_${toVersion}"

$patchRoot = if ([System.IO.Path]::IsPathRooted($PatchRoot)) { $PatchRoot } else { Join-Path $repoRoot $PatchRoot }
$pairDir = Join-Path $patchRoot $pair
$zipPath = Join-Path $pairDir 'patch.zip'
$versionsPath = Join-Path $patchRoot 'versions.txt'

Write-Output "SourceRoot      : $sourceRoot"
Write-Output "TargetRoot      : $targetRoot"
Write-Output "PatchRoot       : $patchRoot"
Write-Output "FromVersion     : $fromVersion"
Write-Output "ToVersion       : $toVersion"
Write-Output "PatchPair       : $pair"
Write-Output "DryRun          : $DryRun"

$targetFiles = Get-ChildItem -LiteralPath $targetRoot -Recurse -File
$entries = New-Object System.Collections.Generic.List[object]

$included = 0
$excluded = 0
$unchanged = 0

foreach ($file in $targetFiles) {
    $relPath = Normalize-RelPath -root $targetRoot -fullPath $file.FullName

    if (Should-Exclude -relativePath $relPath -patterns $ExcludeRegex) {
        $excluded++
        continue
    }

    $sourceFilePath = Join-Path $sourceRoot ($relPath -replace '/', '\\')

    $include = $false
    if (-not (Test-Path -LiteralPath $sourceFilePath)) {
        $include = $true
    }
    else {
        $sourceInfo = Get-Item -LiteralPath $sourceFilePath
        if ($sourceInfo.Length -ne $file.Length) {
            $include = $true
        }
        else {
            $sourceHash = (Get-FileHash -LiteralPath $sourceFilePath -Algorithm SHA256).Hash
            $targetHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            if ($sourceHash -ne $targetHash) {
                $include = $true
            }
        }
    }

    if (-not $include) {
        $unchanged++
        continue
    }

    $entries.Add([PSCustomObject]@{
        SourcePath = $file.FullName
        EntryPath = $relPath
        LastWrite = $file.LastWriteTimeUtc
    }) | Out-Null
    $included++
}

Write-Output "Included files  : $included"
Write-Output "Excluded files  : $excluded"
Write-Output "Unchanged files : $unchanged"

if ($DryRun) {
    $entries | Select-Object -First 20 -ExpandProperty EntryPath | ForEach-Object { Write-Output " + $_" }
    return
}

New-Item -ItemType Directory -Path $pairDir -Force | Out-Null
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($entry in $entries) {
        $zipEntry = $zip.CreateEntry($entry.EntryPath, [System.IO.Compression.CompressionLevel]::Optimal)
        $zipEntry.LastWriteTime = $entry.LastWrite

        $inStream = [System.IO.File]::OpenRead($entry.SourcePath)
        $outStream = $zipEntry.Open()
        try {
            $inStream.CopyTo($outStream)
        }
        finally {
            $outStream.Dispose()
            $inStream.Dispose()
        }
    }
}
finally {
    $zip.Dispose()
}

Set-ZipUtf8Flags -zipPath $zipPath

if ($WriteVersions -or -not (Test-Path -LiteralPath $versionsPath)) {
    Set-Content -LiteralPath $versionsPath -Value "$fromVersion $toVersion" -Encoding UTF8
}

if ($SyncRuntimeResources) {
    $runtimePatchRoot = Join-Path $repoRoot 'server/src/Shadowrun.LocalService.Host/bin/Release/Resources/config/patches'
    $runtimePairDir = Join-Path $runtimePatchRoot $pair
    New-Item -ItemType Directory -Path $runtimePairDir -Force | Out-Null
    Copy-Item -LiteralPath $zipPath -Destination (Join-Path $runtimePairDir 'patch.zip') -Force
    Copy-Item -LiteralPath $versionsPath -Destination (Join-Path $runtimePatchRoot 'versions.txt') -Force
}

$zipSize = (Get-Item -LiteralPath $zipPath).Length
Write-Output "Patch zip        : $zipPath"
Write-Output "Patch size bytes : $zipSize"
Write-Output "Versions file    : $versionsPath"
