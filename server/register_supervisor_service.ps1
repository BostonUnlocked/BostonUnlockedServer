param(
    [string]$ServiceName = "ShadowrunLocalSupervisor",
    [string]$DisplayName = "Shadowrun Local Supervisor",
    [string]$Description = "Discord-driven management supervisor for Shadowrun local service",
    [ValidateSet("Automatic", "Manual", "Disabled")]
    [string]$StartupType = "Automatic",
    [string]$ExePath,
    [string]$ConfigPath,
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
    $ExePath = Join-Path $PSScriptRoot "src\Shadowrun.LocalService.Supervisor\bin\Release\net8.0-windows\win-x64\publish\Shadowrun.LocalService.Supervisor.exe"
}

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $PSScriptRoot "src\Shadowrun.LocalService.Supervisor\bin\Release\net8.0-windows\win-x64\publish\appsettings.json"
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Supervisor executable not found: $ExePath`nPublish first: dotnet publish .\server\src\Shadowrun.LocalService.Supervisor\Shadowrun.LocalService.Supervisor.csproj -c Release -r win-x64"
}

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    throw "Supervisor config not found: $ConfigPath"
}

$binPath = '"{0}" --config "{1}"' -f $ExePath, $ConfigPath
$scStartup = Convert-StartupTypeToScValue -Type $StartupType

$existingService = Get-ExistingService -Name $ServiceName

if ($Remove) {
    if (-not $existingService) {
        Write-Output "[supervisor] '$ServiceName' is not installed."
        return
    }

    if ($existingService.Status -ne 'Stopped') {
        Write-Output "[supervisor] stopping '$ServiceName'..."
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    }

    & sc.exe delete $ServiceName | Out-Null
    Write-Output "[supervisor] removed '$ServiceName'."
    return
}

if (-not $existingService) {
    Write-Output "[supervisor] creating '$ServiceName'..."
    & sc.exe create $ServiceName binPath= $binPath start= $scStartup DisplayName= $DisplayName | Out-Null
}
else {
    if ($existingService.Status -ne 'Stopped') {
        Write-Output "[supervisor] stopping '$ServiceName' for reconfiguration..."
        Stop-Service -Name $ServiceName -Force -ErrorAction Stop
    }

    Write-Output "[supervisor] updating '$ServiceName'..."
    & sc.exe config $ServiceName binPath= $binPath start= $scStartup DisplayName= $DisplayName | Out-Null
}

if (-not [string]::IsNullOrWhiteSpace($Description)) {
    & sc.exe description $ServiceName $Description | Out-Null
}

Write-Output "[supervisor] registered '$ServiceName'."
Write-Output "[supervisor] startup type: $StartupType"
Write-Output "[supervisor] binPath: $binPath"

if ($StartAfterRegister) {
    Write-Output "[supervisor] starting '$ServiceName'..."
    Start-Service -Name $ServiceName -ErrorAction Stop
}
