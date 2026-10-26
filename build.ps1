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
$taskSources = @('src\Ballistics.cs','src\WeaponCatalog.cs','src\MapCatalog.cs','src\MapView.cs','src\Main.cs','src\ServerModels.cs','src\EosClient.cs','src\ServerBrowserView.cs','src\ServerDiagnostics.cs','src\RaasData.cs','src\RaasPrediction.cs','src\RaasView.cs','src\FactionCatalog.cs') | ForEach-Object { Join-Path $taskRoot $_ }
$taskFactionSource = Join-Path $taskRoot 'src\FactionCatalog.cs'
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
# Remove only known obsolete generated files left by builds with Steam joining.
foreach ($taskObsolete in @('Steamworks.NET.dll','steam_api64.dll','third_party\Steamworks.NET\Steamworks.NET.dll','third_party\Steamworks.NET\steam_api64.dll','third_party\Steamworks.NET\LICENSE.txt','third_party\Steamworks.NET\SOURCE.md')) {
    $taskObsoletePath = Join-Path $taskOutput $taskObsolete
    if (Test-Path -LiteralPath $taskObsoletePath -PathType Leaf) { Remove-Item -LiteralPath $taskObsoletePath }
}
$taskObsoleteFolder = Join-Path $taskOutput 'third_party\Steamworks.NET'
if ((Test-Path -LiteralPath $taskObsoleteFolder -PathType Container) -and @(Get-ChildItem -LiteralPath $taskObsoleteFolder -Force).Count -eq 0) { Remove-Item -LiteralPath $taskObsoleteFolder }
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'third_party') -Destination $taskOutput -Recurse -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $taskOutput -Recurse -Force
Copy-Item -LiteralPath $taskMapRoot -Destination $taskOutput -Recurse -Force
$taskLayerRoot = Join-Path $taskRoot 'assets\layers'
if (-not (Test-Path -LiteralPath (Join-Path $taskLayerRoot 'catalog.json'))) { throw 'Bundled RAAS layer catalog is missing.' }
Copy-Item -LiteralPath $taskLayerRoot -Destination $taskOutput -Recurse -Force
Write-Output ('Built: ' + (Join-Path $taskOutput 'SquadTools.exe'))
if ($Test) {
    $taskBuild = Join-Path $taskRoot 'build'
    New-Item -ItemType Directory -Path $taskBuild -Force | Out-Null
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /codepage:65001 ('/out:' + (Join-Path $taskBuild 'CoreTests.exe')) (Join-Path $taskRoot 'src\Ballistics.cs') (Join-Path $taskRoot 'src\WeaponCatalog.cs') (Join-Path $taskRoot 'tests\CoreTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & (Join-Path $taskBuild 'CoreTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /codepage:65001 ('/out:' + (Join-Path $taskBuild 'MapTests.exe')) $taskRefs (Join-Path $taskRoot 'src\Ballistics.cs') (Join-Path $taskRoot 'src\WeaponCatalog.cs') (Join-Path $taskRoot 'src\MapCatalog.cs') (Join-Path $taskRoot 'tests\MapTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Map test compilation failed.' }
    & (Join-Path $taskBuild 'MapTests.exe') (Join-Path $taskOutput 'maps')
    if ($LASTEXITCODE -ne 0) { throw 'Map tests failed.' }
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /codepage:65001 ('/out:' + (Join-Path $taskBuild 'MapRenderTests.exe')) $taskRefs $taskFactionSource (Join-Path $taskRoot 'src\Ballistics.cs') (Join-Path $taskRoot 'src\WeaponCatalog.cs') (Join-Path $taskRoot 'src\MapView.cs') (Join-Path $taskRoot 'src\RaasData.cs') (Join-Path $taskRoot 'src\RaasPrediction.cs') (Join-Path $taskRoot 'src\ServerModels.cs') (Join-Path $taskRoot 'tests\MapRenderTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Map render test compilation failed.' }
    & (Join-Path $taskBuild 'MapRenderTests.exe') (Join-Path $taskMapRoot 'images\blackcoast.jpg') (Join-Path $taskBuild 'map-render-preview.png')
    if ($LASTEXITCODE -ne 0) { throw 'Map render tests failed.' }
    $taskSmoke = Start-Process -FilePath (Join-Path $taskOutput 'SquadTools.exe') -ArgumentList @('--smoke-test', ('"' + (Join-Path $taskBuild 'desktop-preview.png') + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($taskSmoke.ExitCode -ne 0) { throw 'Desktop smoke test failed. See build\desktop-preview.txt.' }
    Get-Content -LiteralPath (Join-Path $taskBuild 'desktop-preview.txt')
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /platform:x64 /codepage:65001 ('/out:' + (Join-Path $taskBuild 'ServerTests.exe')) $taskRefs $taskFactionSource (Join-Path $taskRoot 'src\ServerModels.cs') (Join-Path $taskRoot 'src\EosClient.cs') (Join-Path $taskRoot 'tests\ServerTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Server test compilation failed.' }
    & (Join-Path $taskBuild 'ServerTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Server tests failed.' }
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /platform:x64 /codepage:65001 ('/out:' + (Join-Path $taskBuild 'ServerBrowserTests.exe')) $taskRefs $taskFactionSource (Join-Path $taskRoot 'src\ServerModels.cs') (Join-Path $taskRoot 'src\EosClient.cs') (Join-Path $taskRoot 'src\ServerBrowserView.cs') (Join-Path $taskRoot 'tests\ServerBrowserTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Server browser regression test compilation failed.' }
    & (Join-Path $taskBuild 'ServerBrowserTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Server browser regression tests failed.' }
    $taskServerSmoke = Start-Process -FilePath (Join-Path $taskOutput 'SquadTools.exe') -ArgumentList @('--server-smoke-test', ('"' + (Join-Path $taskBuild 'server-preview.png') + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($taskServerSmoke.ExitCode -ne 0) { throw 'Server desktop smoke test failed. See build\server-preview.txt.' }
    Get-Content -LiteralPath (Join-Path $taskBuild 'server-preview.txt')
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /platform:x64 /main:WeaponAndSyncTests /codepage:65001 ('/out:' + (Join-Path $taskBuild 'WeaponAndSyncTests.exe')) $taskRefs $taskSources (Join-Path $taskRoot 'tests\WeaponAndSyncTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Weapon/map sync test compilation failed.' }
    & (Join-Path $taskBuild 'WeaponAndSyncTests.exe') (Join-Path $taskOutput 'maps') (Join-Path $taskBuild 'weapon-sync-preview.png')
    if ($LASTEXITCODE -ne 0) { throw 'Weapon/map sync tests failed.' }
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /platform:x64 /main:RaasTests /codepage:65001 ('/out:' + (Join-Path $taskBuild 'RaasTests.exe')) $taskRefs $taskSources (Join-Path $taskRoot 'tests\RaasTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'RAAS test compilation failed.' }
    & (Join-Path $taskBuild 'RaasTests.exe') (Join-Path $taskOutput 'layers') (Join-Path $taskOutput 'maps') (Join-Path $taskBuild 'raas-coverage.md') (Join-Path $taskRoot 'tests\fixtures\raas-reference.json') (Join-Path $taskBuild 'raas-preview.png')
    if ($LASTEXITCODE -ne 0) { throw 'RAAS tests failed.' }
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /platform:x64 /main:LayoutTests /codepage:65001 ('/out:' + (Join-Path $taskBuild 'LayoutTests.exe')) $taskRefs $taskSources (Join-Path $taskRoot 'tests\LayoutTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Layout test compilation failed.' }
    & (Join-Path $taskBuild 'LayoutTests.exe') (Join-Path $taskOutput 'maps') $taskBuild
    if ($LASTEXITCODE -ne 0) { throw 'Layout tests failed.' }
}
if ($Benchmark) {
    $taskBuild = Join-Path $taskRoot 'build'
    New-Item -ItemType Directory -Path $taskBuild -Force | Out-Null
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /codepage:65001 ('/out:' + (Join-Path $taskBuild 'RenderBenchmark.exe')) $taskRefs $taskFactionSource (Join-Path $taskRoot 'src\Ballistics.cs') (Join-Path $taskRoot 'src\WeaponCatalog.cs') (Join-Path $taskRoot 'src\MapView.cs') (Join-Path $taskRoot 'src\RaasData.cs') (Join-Path $taskRoot 'src\RaasPrediction.cs') (Join-Path $taskRoot 'src\ServerModels.cs') (Join-Path $taskRoot 'tests\RenderBenchmark.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Render benchmark compilation failed.' }
    $taskMeasurements = & (Join-Path $taskBuild 'RenderBenchmark.exe') (Join-Path $taskMapRoot 'images\blackcoast.jpg')
    if ($LASTEXITCODE -ne 0) { throw 'Render benchmark failed.' }
    $taskMeasurements | Set-Content -LiteralPath (Join-Path $taskBuild 'render-after.txt') -Encoding UTF8
    $taskMeasurements | Write-Output
}
