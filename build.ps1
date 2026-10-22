param([switch]$Test)
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
$taskRefs = @('System.dll','System.Core.dll','System.Xml.dll','System.Xaml.dll') | ForEach-Object { '/reference:' + (Join-Path $taskFramework $_) }
$taskRefs += @('PresentationCore.dll','PresentationFramework.dll','WindowsBase.dll') | ForEach-Object { '/reference:' + (Join-Path (Join-Path $taskFramework 'WPF') $_) }
$taskSources = @('src\Ballistics.cs','src\MapView.cs','src\Main.cs') | ForEach-Object { Join-Path $taskRoot $_ }
& $taskCompiler /nologo /warn:4 /langversion:5 /target:winexe /platform:anycpu /optimize+ /codepage:65001 ('/out:' + (Join-Path $taskOutput 'SquadTools.exe')) $taskRefs $taskSources
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'LICENSE') -Destination $taskOutput -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'third_party') -Destination $taskOutput -Recurse -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $taskOutput -Recurse -Force
Write-Output ('Built: ' + (Join-Path $taskOutput 'SquadTools.exe'))
if ($Test) {
    $taskBuild = Join-Path $taskRoot 'build'
    New-Item -ItemType Directory -Path $taskBuild -Force | Out-Null
    & $taskCompiler /nologo /warn:4 /langversion:5 /target:exe /codepage:65001 ('/out:' + (Join-Path $taskBuild 'CoreTests.exe')) (Join-Path $taskRoot 'src\Ballistics.cs') (Join-Path $taskRoot 'tests\CoreTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
    & (Join-Path $taskBuild 'CoreTests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    $taskSmoke = Start-Process -FilePath (Join-Path $taskOutput 'SquadTools.exe') -ArgumentList @('--smoke-test', ('"' + (Join-Path $taskBuild 'desktop-preview.png') + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($taskSmoke.ExitCode -ne 0) { throw 'Desktop smoke test failed. See build\desktop-preview.txt.' }
    Get-Content -LiteralPath (Join-Path $taskBuild 'desktop-preview.txt')
}
