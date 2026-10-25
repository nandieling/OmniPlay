# 开发用：核心 -> 演示与探针 -> 跑探针。打包请用 package.ps1。
#   pwsh windows/build.ps1 [-Release]
param(
    [switch]$Release,
    [string]$DotnetConfiguration = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Split-Path -Parent $PSScriptRoot
$configuration = if ($Release) { 'Release' } else { 'Debug' }
if (-not $DotnetConfiguration) { $DotnetConfiguration = $configuration }
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

Write-Host '==> swift build（核心）' -ForegroundColor Cyan
Push-Location $root
try {
    swift build --configuration $configuration
    if ($LASTEXITCODE -ne 0) { throw "swift build 失败（$LASTEXITCODE）" }
} finally { Pop-Location }

$core = Find-CoreLibrary -BuildDir (Join-Path $root '.build') -Configuration $configuration
Copy-Item $core (Join-Path $artifacts 'omniplay-core.dll') -Force

$native = Join-Path $PSScriptRoot 'native'
New-Item -ItemType Directory -Force -Path $native | Out-Null
$libmpv = Ensure-LibMpv -TargetDir $native
if ($libmpv) { Copy-Item $libmpv (Join-Path $artifacts 'libmpv-2.dll') -Force }
else { Write-Warning '没有 libmpv-2.dll：播放器会以「不可用」启动' }

Write-Host '==> dotnet build（探针 + 演示）' -ForegroundColor Cyan
dotnet build (Join-Path $PSScriptRoot 'abi-probe\AbiProbe.csproj') -c $DotnetConfiguration -p:OmniPlayNativeDir=$artifacts
if ($LASTEXITCODE -ne 0) { throw 'abi-probe 构建失败' }
dotnet build (Join-Path $PSScriptRoot 'OmniPlay.Wpf\OmniPlay.Wpf.csproj') -c $DotnetConfiguration -p:OmniPlayNativeDir=$artifacts
if ($LASTEXITCODE -ne 0) { throw '演示程序构建失败' }

Write-Host '==> abi-probe' -ForegroundColor Cyan
$tfm = 'net8.0'
& (Join-Path $PSScriptRoot "abi-probe\bin\$DotnetConfiguration\$tfm\abi-probe.exe")
if ($LASTEXITCODE -ne 0) { throw "abi-probe 失败（$LASTEXITCODE）" }

Write-Host '全部通过。演示程序：' -ForegroundColor Green
Write-Host ('  ' + (Join-Path $PSScriptRoot "OmniPlay.Wpf\bin\$DotnetConfiguration\net8.0-windows\OmniPlaySpike.exe"))
