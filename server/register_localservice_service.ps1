param(
    [string]$ServiceName = "ShadowrunLocalService",
    [string]$DisplayName = "Shadowrun Local Service",
    [string]$Description = "Offline Shadowrun Chronicles local service host",
    [ValidateSet("Automatic", "Manual", "Disabled")]
    [string]$StartupType = "Automatic",
    [string]$BindHost = "0.0.0.0",
    [int]$Port = 80,
    [int]$APlayPort = 5055,
    [int]$PhotonPort = 4530,
    [switch]$NoFileLogs,
    [switch]$FixedSeed,
    [switch]$UseJson,
    [switch]$MigrateJsonToSqlite,
    [string]$SQLiteDbPath,
    [string]$ExePath,
    [switch]$StartAfterRegister,
    [switch]$Remove
)

$ErrorActionPreference = "Stop"

function Convert-StartupTypeToScValue {
    param([string]$Type)
    switch ($Type) {
        "Automatic" { return "auto" }
        "Manual" { return "demand" }
        "Disabled" { return "disabled" }
        default { return "demand" }
    }
}

function Get-ExistingService {
    param([string]$Name)
    try {
          return Get-Service -Name $Name -ErrorAction Stop
    }
    catch {
        return $null
    }
}

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $PSScriptRoot "src\Shadowrun.LocalService.Host\bin\Release\Shadowrun.LocalService.Host.exe"
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Host executable not found: $ExePath`nBuild first with .\\start_localserver.ps1 or MSBuild."
}

$effectiveUseSqlite = -not $UseJson

$argTokens = @(
    '--service',
    '--host', $BindHost,
    '--port', $Port,
    '--aplay-port', $APlayPort,
    '--photon-port', $PhotonPort
)

if ($NoFileLogs) {
    $argTokens += '--no-file-logs'
}

if ($FixedSeed) {
    $argTokens += '--fixed-seed'
}

if ($effectiveUseSqlite) {
    $argTokens += '--use-sqlite'
}
else {
    $argTokens += '--use-json'
}

if ($MigrateJsonToSqlite) {
    $argTokens += '--migrate-json-to-sqlite'
}

if (-not [string]::IsNullOrWhiteSpace($SQLiteDbPath)) {
    $argTokens += '--sqlite-db-path'
    $argTokens += $SQLiteDbPath
}

$quotedArgs = $argTokens | ForEach-Object {
    if ($_ -match '\s') {
        '"{0}"' -f ($_ -replace '"', '\"')
    }
    else {
        $_
    }
}

$binPath = '"{0}" {1}' -f $ExePath, ($quotedArgs -join ' ')
$scStartup = Convert-StartupTypeToScValue -Type $StartupType

$existingService = Get-ExistingService -Name $ServiceName

if ($Remove) {
    if (-not $existingService) {
        Write-Output "[service] '$ServiceName' is not installed."
        return
    }

    if ($existingService.Status -ne 'Stopped') {
        Write-Output "[service] stopping '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    }

    & sc.exe delete $ServiceName | Out-Null
    Write-Output "[service] removed '$ServiceName'."
    return
}

if (-not $existingService) {
    Write-Output "[service] creating '$ServiceName'..."
    & sc.exe create $ServiceName binPath= $binPath start= $scStartup DisplayName= $DisplayName | Out-Null
}
else {
    if ($existingService.Status -ne 'Stopped') {
        Write-Output "[service] stopping '$ServiceName' for reconfiguration..."
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    }

    Write-Output "[service] updating '$ServiceName'..."
    & sc.exe config $ServiceName binPath= $binPath start= $scStartup DisplayName= $DisplayName | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($Description)) {
    & sc.exe description $ServiceName $Description | Out-Null
}

Write-Output "[service] registered '$ServiceName'."
Write-Output "[service] startup type: $StartupType"
Write-Output "[service] binPath: $binPath"

if ($StartAfterRegister) {
    Write-Output "[service] starting '$ServiceName'..."
    Start-Service -Name $ServiceName -ErrorAction Stop
}
