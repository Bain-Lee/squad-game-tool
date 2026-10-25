$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskDist = [IO.Path]::GetFullPath((Join-Path $taskRoot 'dist'))
$taskArchive = Join-Path $taskRoot 'SquadTools-portable.zip'
$taskTemp = Join-Path $taskRoot ('SquadTools-package-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$taskFiles = @()
foreach ($taskName in @('SquadTools.exe','Steamworks.NET.dll','steam_api64.dll','README.md','LICENSE')) {
    $taskPath = Join-Path $taskDist $taskName
    if (-not (Test-Path -LiteralPath $taskPath -PathType Leaf)) { throw ('Missing package file: ' + $taskName + '. Run build.ps1 first.') }
    $taskFiles += Get-Item -LiteralPath $taskPath
}
foreach ($taskName in @('maps','docs','third_party')) {
    $taskPath = Join-Path $taskDist $taskName
    if (-not (Test-Path -LiteralPath $taskPath -PathType Container)) { throw ('Missing package directory: ' + $taskName) }
    $taskEntries = Get-ChildItem -LiteralPath $taskPath -Recurse
    if ($taskEntries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw ('Package directory contains a link: ' + $taskName) }
    $taskFiles += $taskEntries | Where-Object { -not $_.PSIsContainer }
}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    $taskZip = [IO.Compression.ZipFile]::Open($taskTemp, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($taskFile in $taskFiles) {
            $taskResolved = [IO.Path]::GetFullPath($taskFile.FullName)
            if (-not $taskResolved.StartsWith($taskDist + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package input escaped the dist directory.' }
            $taskEntry = $taskResolved.Substring($taskDist.Length + 1).Replace('\','/')
            if ($taskEntry -match '(^|/)(data|build)/' -or $taskEntry -match '\.(tmp|part)$') { throw ('Unexpected package content: ' + $taskEntry) }
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip, $taskResolved, $taskEntry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $taskZip.Dispose() }
    $taskZip = [IO.Compression.ZipFile]::OpenRead($taskTemp)
    try {
        if ($taskZip.Entries.Count -ne $taskFiles.Count) { throw 'Package file count mismatch.' }
        foreach ($taskRequired in @('SquadTools.exe','Steamworks.NET.dll','steam_api64.dll','maps/catalog.xml','docs/SERVER_BROWSER.md')) {
            if (-not $taskZip.GetEntry($taskRequired)) { throw ('Package verification failed: ' + $taskRequired) }
        }
        if ($taskZip.Entries | Where-Object { $_.FullName -like 'data/*' }) { throw 'User settings must not be packaged.' }
    } finally { $taskZip.Dispose() }
    if (Test-Path -LiteralPath $taskArchive) { [IO.File]::Replace($taskTemp, $taskArchive, [System.Management.Automation.Language.NullString]::Value) } else { [IO.File]::Move($taskTemp, $taskArchive) }
    Write-Output ('Packaged ' + $taskFiles.Count + ' files; user settings excluded: ' + $taskArchive)
    Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256 | Select-Object Hash, Path
} finally {
    if (Test-Path -LiteralPath $taskTemp) { Remove-Item -LiteralPath $taskTemp }
}
