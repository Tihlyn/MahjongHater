param(
    [string]$Python = 'python',
    # PyTorch wheel index. cu126 covers every CUDA-capable driver from 2024 on; use
    # 'https://download.pytorch.org/whl/cpu' to run the pipeline without a GPU.
    [string]$TorchIndex = 'https://download.pytorch.org/whl/cu126'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $root 'Precompute.exe'))) {
    throw 'Run this script from the portable package produced by tools/publish_training.ps1.'
}

$version = & $Python -c "import sys; print('%d.%d' % sys.version_info[:2])"
if ($LASTEXITCODE -ne 0) { throw "Python not found as '$Python'. Install Python 3.10-3.13 (64-bit) and add it to PATH, or pass -Python <path>." }
if ([version]$version -lt [version]'3.10') { throw "Python $version is too old; 3.10 or newer is required." }

$venv = Join-Path $root '.venv'
if (-not (Test-Path -LiteralPath $venv)) {
    & $Python -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw 'Creating the virtual environment failed.' }
}
$pip = Join-Path $venv 'Scripts/python.exe'
& $pip -m pip install --upgrade pip
& $pip -m pip install --index-url $TorchIndex torch
if ($LASTEXITCODE -ne 0) { throw "Installing torch from $TorchIndex failed." }
& $pip -m pip install -r (Join-Path $root 'requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Installing requirements failed.' }

& $pip (Join-Path $root 'check_env.py')
if ($LASTEXITCODE -ne 0) { throw 'torch import failed.' }
Write-Host 'Setup complete. Next: .\Run-Training.ps1 (see README.md for stages and sizes).'
