# SPDX-License-Identifier: GPL-3.0-only
# Builds the Rust OCR worker and places it (with its native DLLs) where StarChat.exe looks for it.
# Requires rustup (stable-x86_64-pc-windows-msvc) and Visual Studio 2022 Build Tools with the C++ workload.
#   .\build.ps1              OCR worker only
#   .\build.ps1 -Test        also run the unit tests
#   .\build.ps1 -Translator  also build the experimental translation worker (CTranslate2 + oneDNN from
#                            source, needs CMake, several minutes). It is NOT copied into the app:
#                            it measured slower than the Python worker and its output differs.
param([switch]$Test, [switch]$Translator)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cargoBin = Join-Path $env:USERPROFILE '.cargo\bin'
if (Test-Path (Join-Path $cargoBin 'cargo.exe')) { $env:Path = "$cargoBin;$env:Path" }
if ($Translator -and -not (Get-Command cmake -ErrorAction SilentlyContinue)) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $studio = & $vswhere -products * -latest -property installationPath
        $cmake = Join-Path $studio 'Common7\IDE\CommonExtensions\Microsoft\CMake'
        if (Test-Path (Join-Path $cmake 'CMake\bin\cmake.exe')) { $env:Path = "$cmake\CMake\bin;$cmake\Ninja;$env:Path" }
    }
    if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) { throw 'CMake was not found. Install the Visual Studio C++ build tools.' }
}
# Static CRT (matches .cargo\config.toml) and no developer paths inside the released binaries.
$separator = [string][char]0x1f
$env:CARGO_ENCODED_RUSTFLAGS = @('-C', 'target-feature=+crt-static',
    "--remap-path-prefix=$env:USERPROFILE=~", "--remap-path-prefix=$root=starchat") -join $separator
$scope = if ($Translator) { @('--workspace') } else { @() }
Push-Location $PSScriptRoot
# cargo reports progress on stderr; Windows PowerShell would treat that as a failure.
$ErrorActionPreference = 'Continue'
try {
    if ($Test) { & cargo test --release @scope; if ($LASTEXITCODE) { throw 'cargo test failed' } }
    & cargo build --release @scope
    if ($LASTEXITCODE) { throw 'cargo build failed' }
} finally { Pop-Location; $ErrorActionPreference = 'Stop' }

$ocr = Join-Path $root 'ocr'
Copy-Item (Join-Path $PSScriptRoot 'target\release\starchat-ocr.exe') (Join-Path $ocr 'starchat-ocr.exe') -Force
# ONNX Runtime + DirectML come from the pinned onnxruntime-directml wheel.
$capi = Join-Path $ocr 'runtime\Lib\site-packages\onnxruntime\capi'
foreach ($name in 'onnxruntime.dll', 'onnxruntime_providers_shared.dll', 'DirectML.dll') {
    $source = Join-Path $capi $name
    $target = Join-Path $ocr $name
    if ((Test-Path $source) -and -not (Test-Path $target)) { Copy-Item $source $target }
}
Write-Host 'Rust OCR worker built: ocr\starchat-ocr.exe'
if ($Translator) { Write-Host 'Experimental translator: rust\target\release\starchat-translate.exe (not installed)' }
