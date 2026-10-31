# Builds SwyfAiPatcher.
#
#   .\build.ps1                 framework-dependent single file (~1.3 MB, needs .NET 7 Desktop Runtime)
#   .\build.ps1 -SelfContained  self-contained single file (~68 MB, runs on any Windows x64)
#
# Output lands in .\dist\

param(
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$proj = Join-Path $PSScriptRoot "SwyfAiPatcher.csproj"
$dist = Join-Path $PSScriptRoot "dist"

if ($SelfContained) {
    Write-Host "building self-contained single file..."
    dotnet publish $proj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true --nologo
    $src = Join-Path $PSScriptRoot "bin\Release\net7.0-windows\win-x64\publish"
} else {
    Write-Host "building framework-dependent single file..."
    dotnet publish $proj -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true --nologo
    $src = Join-Path $PSScriptRoot "bin\Release\net7.0-windows\win-x64\publish"
}

New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item (Join-Path $src "SwyfAiPatcher.exe") $dist -Force

$size = [math]::Round((Get-Item (Join-Path $dist "SwyfAiPatcher.exe")).Length / 1MB, 2)
Write-Host ""
Write-Host "done: dist\SwyfAiPatcher.exe ($size MB)"
if (-not $SelfContained) {
    Write-Host "note: this build needs the .NET 7 Desktop Runtime on the target machine."
}
