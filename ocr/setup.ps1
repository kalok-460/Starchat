# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
param([switch]$SkipModelCheck)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$ocrRoot = $PSScriptRoot
$runtime = Join-Path $ocrRoot 'runtime'
$python = Join-Path $runtime 'python.exe'
function Get-VerifiedFile([string]$Url, [string]$Path, [string]$Sha256) {
    if (!(Test-Path -LiteralPath $Path) -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) {
        Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Sha256) { throw "Download checksum mismatch: $Path" }
}
if (!(Test-Path -LiteralPath $python)) {
    $archive = Join-Path $ocrRoot 'python-3.12.10-embed-amd64.zip'
    Get-VerifiedFile 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip' $archive '4ACBED6DD1C744B0376E3B1CF57CE906F9DC9E95E68824584C8099A63025A3C3'
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $runtime -Force
}
$sitePackages = Join-Path $runtime 'Lib\site-packages'
New-Item -ItemType Directory -Force -Path $sitePackages | Out-Null
@('python312.zip', '.', 'Lib\site-packages', '..', 'import site') | Set-Content -LiteralPath (Join-Path $runtime 'python312._pth') -Encoding ascii
if (!(Test-Path -LiteralPath (Join-Path $sitePackages 'pip'))) {
    $pipArchive = Join-Path $ocrRoot 'pip-25.3.zip'
    Get-VerifiedFile 'https://files.pythonhosted.org/packages/44/3c/d717024885424591d5376220b5e836c2d5293ce2011523c9de23ff7bf068/pip-25.3-py3-none-any.whl' $pipArchive '9655943313a94722b7774661c21049070f6bbb0a1516bf02f7c8d5d9201514cd'
    Expand-Archive -LiteralPath $pipArchive -DestinationPath $sitePackages -Force
}
# This installation affects only ocr/runtime, never system Python or PATH.
$env:PIP_DISABLE_PIP_VERSION_CHECK = '1'
$env:PIP_NO_CACHE_DIR = '1'
$requirements = Join-Path $ocrRoot 'requirements.lock.txt'
if (!(Test-Path -LiteralPath $requirements)) { $requirements = Join-Path $ocrRoot 'requirements.txt' }
& $python -m pip --isolated install --index-url https://pypi.org/simple --only-binary=:all: --no-warn-script-location -r $requirements
if ($LASTEXITCODE -ne 0) { throw 'OCR dependency installation failed.' }
& $python -m pip freeze | Set-Content -LiteralPath (Join-Path $ocrRoot 'installed-versions.txt') -Encoding utf8
& $python (Join-Path $ocrRoot 'download_models.py')
if ($LASTEXITCODE -ne 0) { throw 'ONNX model download/verification failed.' }
& $python (Join-Path $ocrRoot 'worker.py') --self-test
if ($LASTEXITCODE -ne 0) { throw 'OCR preprocessing self-test failed.' }
if (!$SkipModelCheck) {
    & $python (Join-Path $ocrRoot 'worker.py') --check
    if ($LASTEXITCODE -ne 0) { throw 'PP-OCRv6 small ONNX initialization/inference failed.' }
}
Write-Output 'StarChat OCR setup complete.'

