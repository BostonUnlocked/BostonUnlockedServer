param(
    [string]$AssemblyDirectory = (Join-Path $PSScriptRoot '..\server\src\Shadowrun.LocalService.Host\bin\Release'),
    [switch]$MeasurePerformance
)

$ErrorActionPreference = 'Stop'
$AssemblyDirectory = (Resolve-Path -LiteralPath $AssemblyDirectory).Path
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('BostonUnlocked-social-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDirectory | Out-Null

try {
    $executable = Join-Path $outputDirectory 'SocialPresenceRegression.exe'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\server\src\Dependencies\sqlite3.dll') -Destination $outputDirectory
    & $compiler /nologo /target:exe /platform:x64 "/out:$executable" `
        /reference:System.Web.Extensions.dll `
        "/reference:$AssemblyDirectory\Shadowrun.LocalService.Core.dll" `
        "/reference:$AssemblyDirectory\Cliffhanger.SRO.ServerClientCommons.dll" `
        "/reference:$AssemblyDirectory\SRO.Core.Compatibility.dll" `
        "/reference:$AssemblyDirectory\Cliffhanger.Core.Compatibility.dll" `
        "/reference:$AssemblyDirectory\JsonFx.Json.dll" `
        "/reference:$AssemblyDirectory\PhotonProxy.Common.dll" `
        "/reference:$AssemblyDirectory\PhotonProxy.Serializer.Client.dll" `
        "/reference:$AssemblyDirectory\protobuf-net.dll" `
        "/reference:$AssemblyDirectory\PhotonProxy.ChatAndFriends.Client.DTO.dll" `
        (Join-Path $PSScriptRoot 'SocialPresenceRegression.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Could not compile social presence regressions.' }

    foreach ($backend in @('json', 'sqlite')) {
        & $executable $AssemblyDirectory (Join-Path $outputDirectory $backend) ($backend -eq 'sqlite') $MeasurePerformance.IsPresent
        if ($LASTEXITCODE -ne 0) { throw "Social presence regressions failed ($backend)." }
    }
}
finally {
    Remove-Item -LiteralPath $outputDirectory -Recurse -Force
}
