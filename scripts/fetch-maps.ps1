param(
    [string]$PythonPath,
    [string]$SourceCommit = 'master',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $PythonPath) {
    $bundledPython = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
    if (Test-Path -LiteralPath $bundledPython) {
        $PythonPath = $bundledPython
    } else {
        $existingPython = Get-Command python -ErrorAction SilentlyContinue
        if ($existingPython) { $PythonPath = $existingPython.Source }
    }
}
if (-not $PythonPath) {
    throw 'No existing Python was found. Supply -PythonPath with a Python environment containing Pillow and WebP support. Nothing is installed automatically.'
}

$scriptArgs = @((Join-Path $PSScriptRoot 'fetch-maps.py'), '--output', (Join-Path $projectRoot 'assets\maps'), '--source-commit', $SourceCommit)
if ($Force) { $scriptArgs += '--force' }
& $PythonPath @scriptArgs
if ($LASTEXITCODE -ne 0) { throw "Map download failed with exit code $LASTEXITCODE." }
