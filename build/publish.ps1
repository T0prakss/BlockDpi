$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\BlockDpi\BlockDpi.csproj'
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
Write-Host "==> 1/3 Windows x64 (.exe) derleniyor..."
$winDir = Join-Path $dist 'windows-x64'
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $winDir
foreach ($arch in @('osx-arm64', 'osx-x64')) {
    Write-Host "==> macOS ($arch) .app paketi oluşturuluyor..."
    $pubTmp = Join-Path $dist "_tmp_$arch"
    dotnet publish $proj -c Release -r $arch --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugType=None -p:DebugSymbols=false `
        -o $pubTmp
    $appDir = Join-Path $dist "macos-$arch\BlockDpi.app\Contents"
    New-Item -ItemType Directory -Force (Join-Path $appDir 'MacOS') | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $appDir 'Resources') | Out-Null
    Copy-Item (Join-Path $PSScriptRoot 'macos\Info.plist') (Join-Path $appDir 'Info.plist') -Force
    Copy-Item (Join-Path $PSScriptRoot 'macos\icon.icns') (Join-Path $appDir 'Resources\icon.icns') -Force
    Copy-Item -Recurse -Force (Join-Path $pubTmp '*') (Join-Path $appDir 'MacOS')
    Remove-Item -Recurse -Force $pubTmp
}
Write-Host "==> Tamamlandı! Çıktılar: $dist"
