param([switch]$Test, [switch]$Benchmark)
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $taskFramework 'csc.exe'))) {
    $taskFramework = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$taskCompiler = Join-Path $taskFramework 'csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'Windows .NET Framework C# compiler was not found. No software was installed.' }
$taskOutput = Join-Path $taskRoot 'dist'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskRefs = @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll','System.Net.Http.dll','System.Web.Extensions.dll') | ForEach-Object { '/reference:' + (Join-Path $taskFramework $_) }
$taskRefs += @('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path (Join-Path $taskFramework 'WPF') $_) }
$taskRefs += '/reference:' + (Join-Path $taskRoot 'third_party\Steamworks.NET\Steamworks.NET.dll')
$taskSources = @('src\Ballistics.cs','src\MapCatalog.cs','src\MapView.cs','src\Main.cs','src\ServerModels.cs','src\EosClient.cs','src\ServerBrowserView.cs','src\SteamJoinBridge.cs','src\ServerDiagnostics.cs') | ForEach-Object { Join-Path $taskRoot $_ }
$taskMapRoot = Join-Path $taskRoot 'assets\maps'
if (-not (Test-Path -LiteralPath (Join-Path $taskMapRoot 'catalog.xml'))) { throw 'Map catalog missing. Run scripts\fetch-maps.ps1 with an existing Python/Pillow environment first.' }
[xml]$taskMapCatalog = Get-Content -LiteralPath (Join-Path $taskMapRoot 'catalog.xml') -Raw -Encoding UTF8
foreach ($taskMap in $taskMapCatalog.maps.map) {
    foreach ($taskAsset in @($taskMap.image, $taskMap.heightmap)) {
        if ($taskAsset -and -not (Test-Path -LiteralPath (Join-Path $taskMapRoot $taskAsset))) { throw ('Missing offline map: ' + $taskAsset + '. Run scripts\fetch-maps.ps1. Nothing is installed automatically.') }
    }
}
& $taskCompiler /nologo /warn:4 /langversion:5 /target:winexe /platform:x64 /optimize+ /codepage:65001 ('/out:' + (Join-Path $taskOutput 'SquadTools.exe')) $taskRefs $taskSources
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'third_party') -Destination $taskOutput -Recurse -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'third_party\Steamworks.NET\Steamworks.NET.dll'),(Join-Path $taskRoot 'third_party\Steamworks.NET\steam_api64.dll') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $taskOutput -Recurse -Force
Copy-Item -LiteralPath $taskMapRoot -Destination $taskOutput -Recurse -Force
Write-Output ('Built: ' + (Join-Path $taskOutput 'SquadTools.exe'))
if ($Test) {
    $taskBuild = Join-Path $taskRoot 'build'
    New-Item -ItemType Directory -Path $taskBuild -Force | Out-Null
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /codepage:65001 ('/out:' + (Join-Path $taskBuild 'CoreTests.exe')) (Join-Path $taskRoot 'src\Ballistics.cs') (Join-Path $taskRoot 'tests\CoreTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & (Join-Path $taskBuild 'CoreTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /codepage:65001 ('/out:' + (Join-Path $taskBuild 'MapTests.exe')) $taskRefs (Join-Path $taskRoot 'src\Ballistics.cs') (Join-Path $taskRoot 'src\MapCatalog.cs') (Join-Path $taskRoot 'tests\MapTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Map test compilation failed.' }
    & (Join-Path $taskBuild 'MapTests.exe') (Join-Path $taskOutput 'maps')
    if ($LASTEXITCODE -ne 0) { throw 'Map tests failed.' }
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /codepage:65001 ('/out:' + (Join-Path $taskBuild 'MapRenderTests.exe')) $taskRefs (Join-Path $taskRoot 'src\Ballistics.cs') (Join-Path $taskRoot 'src\MapView.cs') (Join-Path $taskRoot 'tests\MapRenderTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Map render test compilation failed.' }
    & (Join-Path $taskBuild 'MapRenderTests.exe') (Join-Path $taskMapRoot 'images\blackcoast.jpg') (Join-Path $taskBuild 'map-render-preview.png')
    if ($LASTEXITCODE -ne 0) { throw 'Map render tests failed.' }
    $taskSmoke = Start-Process -FilePath (Join-Path $taskOutput 'SquadTools.exe') -ArgumentList @('--smoke-test', ('"' + (Join-Path $taskBuild 'desktop-preview.png') + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($taskSmoke.ExitCode -ne 0) { throw 'Desktop smoke test failed. See build\desktop-preview.txt.' }
    Get-Content -LiteralPath (Join-Path $taskBuild 'desktop-preview.txt')
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /platform:x64 /codepage:65001 ('/out:' + (Join-Path $taskBuild 'ServerTests.exe')) $taskRefs (Join-Path $taskRoot 'src\ServerModels.cs') (Join-Path $taskRoot 'src\EosClient.cs') (Join-Path $taskRoot 'src\SteamJoinBridge.cs') (Join-Path $taskRoot 'tests\ServerTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Server test compilation failed.' }
    Copy-Item -LiteralPath (Join-Path $taskRoot 'third_party\Steamworks.NET\Steamworks.NET.dll') -Destination $taskBuild -Force
    & (Join-Path $taskBuild 'ServerTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Server tests failed.' }
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /platform:x64 /codepage:65001 ('/out:' + (Join-Path $taskBuild 'ServerBrowserTests.exe')) $taskRefs (Join-Path $taskRoot 'src\ServerModels.cs') (Join-Path $taskRoot 'src\EosClient.cs') (Join-Path $taskRoot 'src\SteamJoinBridge.cs') (Join-Path $taskRoot 'src\ServerBrowserView.cs') (Join-Path $taskRoot 'tests\ServerBrowserTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Server browser regression test compilation failed.' }
    & (Join-Path $taskBuild 'ServerBrowserTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Server browser regression tests failed.' }
    $taskServerSmoke = Start-Process -FilePath (Join-Path $taskOutput 'SquadTools.exe') -ArgumentList @('--server-smoke-test', ('"' + (Join-Path $taskBuild 'server-preview.png') + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($taskServerSmoke.ExitCode -ne 0) { throw 'Server desktop smoke test failed. See build\server-preview.txt.' }
    Get-Content -LiteralPath (Join-Path $taskBuild 'server-preview.txt')
    $taskJoinStart = New-Object Diagnostics.ProcessStartInfo
    $taskJoinStart.FileName = Join-Path $taskOutput 'SquadTools.exe'
    $taskJoinStart.Arguments = '--join-server invalid bad'
    $taskJoinStart.UseShellExecute = $false
    $taskJoinStart.CreateNoWindow = $true
    $taskJoinStart.RedirectStandardOutput = $true
    $taskJoinStart.RedirectStandardError = $true
    $taskJoinStart.StandardOutputEncoding = New-Object Text.UTF8Encoding($false)
    $taskJoinCheck = [Diagnostics.Process]::Start($taskJoinStart)
    try {
        if (-not $taskJoinCheck.WaitForExit(8000)) { $taskJoinCheck.Kill(); throw 'Join helper failed to exit on invalid input.' }
        $taskJoinResult = $taskJoinCheck.StandardOutput.ReadToEnd().Trim()
        if ($taskJoinCheck.ExitCode -ne 1 -or -not $taskJoinResult.StartsWith('SQUADTOOLS_JOIN:ERROR:')) { throw 'Join helper error channel failed.' }
        Write-Output 'PASS: GUI-subsystem join helper returns UTF-8 error and exits; no Steam initialization.'
    } finally { $taskJoinCheck.Dispose() }
}
if ($Benchmark) {
    $taskBuild = Join-Path $taskRoot 'build'
    New-Item -ItemType Directory -Path $taskBuild -Force | Out-Null
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /codepage:65001 ('/out:' + (Join-Path $taskBuild 'RenderBenchmark.exe')) $taskRefs (Join-Path $taskRoot 'src\Ballistics.cs') (Join-Path $taskRoot 'src\MapView.cs') (Join-Path $taskRoot 'tests\RenderBenchmark.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Render benchmark compilation failed.' }
    $taskMeasurements = & (Join-Path $taskBuild 'RenderBenchmark.exe') (Join-Path $taskMapRoot 'images\blackcoast.jpg')
    if ($LASTEXITCODE -ne 0) { throw 'Render benchmark failed.' }
    $taskMeasurements | Set-Content -LiteralPath (Join-Path $taskBuild 'render-after.txt') -Encoding UTF8
    $taskMeasurements | Write-Output
}
