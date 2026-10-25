# 出包：核心（Release）-> 自包含发布 -> Inno 安装包 + 便携 zip + sha256
#   pwsh windows/package.ps1 -Version 0.1.0
param(
    [string]$Version = '0.1.0',
    [string]$Configuration = 'Release',
    [switch]$FrameworkDependent,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
if (-not $Configuration) { $Configuration = 'Release' }
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
Remove-Item $publish, (Join-Path $artifacts 'libomniplay-core.dylib') -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $artifacts, $publish | Out-Null

# swift 认小写配置名，dotnet 用标准大小写
$swiftConfiguration = $Configuration.ToLowerInvariant()
Write-Host "==> swift build -c $swiftConfiguration" -ForegroundColor Cyan
Push-Location $root
try {
    swift build -c $swiftConfiguration
    if ($LASTEXITCODE -ne 0) { throw "swift build 失败（$LASTEXITCODE）" }
    swift test
    if ($LASTEXITCODE -ne 0) { throw '契约测试没过，不出包' }
} finally { Pop-Location }

$core = Find-CoreLibrary -BuildDir (Join-Path $root '.build') -Configuration $Configuration
Copy-Item $core (Join-Path $artifacts 'omniplay-core.dll') -Force
$libmpv = Ensure-LibMpv -TargetDir (Join-Path $PSScriptRoot 'native')
if ($libmpv) { Copy-Item $libmpv (Join-Path $artifacts 'libmpv-2.dll') -Force }
else { Write-Warning '没有 libmpv：安装包里的播放器会显示「不可用」' }

Write-Host '==> dotnet publish' -ForegroundColor Cyan
$publishArgs = @(
    (Join-Path $PSScriptRoot 'OmniPlay.Wpf\OmniPlay.Wpf.csproj'),
    '-c', $Configuration, '-o', $publish, '-p:OmniPlayNativeDir=' + $artifacts, '--nologo'
)
if (-not $FrameworkDependent) { $publishArgs += @('-r', 'win-x64', '--self-contained', 'true') }
dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

# 冒烟：探针跑不过就不出包（与 mac 侧 pack_mac.sh 同一个态度）
Write-Host '==> abi-probe 冒烟' -ForegroundColor Cyan
dotnet build (Join-Path $PSScriptRoot 'abi-probe\AbiProbe.csproj') -c $Configuration -p:OmniPlayNativeDir=$artifacts | Out-Null
Copy-Item (Join-Path $artifacts 'omniplay-core.dll') (Join-Path $PSScriptRoot "abi-probe\bin\$Configuration\net8.0\") -Force
if (Test-Path (Join-Path $artifacts 'libmpv-2.dll')) {
    Copy-Item (Join-Path $artifacts 'libmpv-2.dll') (Join-Path $PSScriptRoot "abi-probe\bin\$Configuration\net8.0\") -Force
}
& (Join-Path $PSScriptRoot "abi-probe\bin\$Configuration\net8.0\abi-probe.exe")
if ($LASTEXITCODE -ne 0) { throw "abi-probe 失败（$LASTEXITCODE），不出包" }

$name = "OmniPlay-win-swift-$Version-x64"
$zip = Join-Path $artifacts "$name-portable.zip"
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip -Force

if (-not $SkipInstaller) {
    $iscc = Find-InnoSetup
    if ($iscc) {
        Write-Host '==> Inno Setup' -ForegroundColor Cyan
        & $iscc "/DAppVersion=$Version" ("/DStageDir=" + $publish) (Join-Path $PSScriptRoot 'Installer.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Inno Setup 打包失败' }
    } else {
        Write-Warning '没找到 Inno Setup：只出便携包'
    }
}

foreach ($file in Get-ChildItem $artifacts -Include '*.zip', '*setup.exe' -Recurse) {
    $hash = (Get-FileHash -Algorithm SHA256 $file.FullName).Hash.ToLower()
    Set-Content -Path "$($file.FullName).sha256" -Value "$($file.Name)  $hash"
    Write-Host ("  {0}  {1:N0} 字节" -f $file.Name, $file.Length)
}
Write-Host '出包完成' -ForegroundColor Green
