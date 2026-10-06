[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")][string]$Configuration = "Release",
    [string]$MSBuildPath
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
if (-not $MSBuildPath) {
    $VsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio/Installer/vswhere.exe"
    if (-not (Test-Path $VsWhere)) { throw "vswhere is unavailable. Supply -MSBuildPath explicitly." }
    $MSBuildPath = & $VsWhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild/Current/Bin/MSBuild.exe' | Select-Object -First 1
}
if (-not $MSBuildPath -or -not (Test-Path $MSBuildPath)) { throw "MSBuild was not found." }
$env:PATH = "$env:USERPROFILE/.cargo/bin;$env:PATH"
& cargo build --locked --release --manifest-path "$Root/Cargo.toml" -p ue-lsp
if ($LASTEXITCODE -ne 0) { throw "Rust language server build failed." }
& $MSBuildPath "$Root/PenguinExtention.sln" -restore "-p:Configuration=$Configuration" -verbosity:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "VSIX build failed." }
& python "$PSScriptRoot/verify_package.py" visual-studio "$Root/bin/$Configuration/PenguinExtention.vsix"
if ($LASTEXITCODE -ne 0) { throw "VSIX package verification failed." }
Write-Host "Built and inspected a local package. Nothing was installed or published."
