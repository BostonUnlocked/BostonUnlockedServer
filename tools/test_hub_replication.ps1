param(
    [string]$AssemblyDirectory = (Join-Path $PSScriptRoot '..\server\src\Shadowrun.LocalService.Host\bin\Release')
)

$ErrorActionPreference = 'Stop'
$AssemblyDirectory = (Resolve-Path -LiteralPath $AssemblyDirectory).Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('BostonUnlocked-hub-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDirectory | Out-Null

try {
    $executable = Join-Path $outputDirectory 'HubReplicationRegression.exe'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\server\src\Dependencies\sqlite3.dll') -Destination $outputDirectory
    & $compiler /nologo /target:exe /platform:x64 "/out:$executable" `
        /reference:System.Web.Extensions.dll `
        "/reference:$AssemblyDirectory\Shadowrun.LocalService.Core.dll" `
        "/reference:$AssemblyDirectory\Cliffhanger.SRO.ServerClientCommons.dll" `
        "/reference:$AssemblyDirectory\SRO.Core.Compatibility.dll" `
        "/reference:$AssemblyDirectory\Cliffhanger.Core.Compatibility.dll" `
        "/reference:$AssemblyDirectory\Cliffhanger.GameLogic.dll" `
        "/reference:$AssemblyDirectory\JsonFx.Json.dll" `
        (Join-Path $PSScriptRoot 'HubReplicationRegression.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile hub replication regressions.' }

    foreach ($backend in @('json', 'sqlite')) {
        & $executable $AssemblyDirectory (Join-Path $outputDirectory $backend) ($backend -eq 'sqlite')
        if ($LASTEXITCODE -ne 0) { throw "Hub replication regressions failed ($backend)." }
    }
}
finally {
    Remove-Item -LiteralPath $outputDirectory -Recurse -Force
}
