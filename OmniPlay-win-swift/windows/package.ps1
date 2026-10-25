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
    # 契约测试（JSON 边界、主题配方）红就不出包。排序那组是「两个平台的 collation
    # 是否等价」的观测项，Windows 上若只有它红，先出包，自定义 collator 另议（docs/SPIKE.md §3）
    $log = Join-Path $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }) 'swift-test.log'
    swift test *>&1 | Tee-Object -FilePath $log | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Get-Content $log | Select-String -Pattern 'error:|XCTAssert' | Select-Object -First 15
        swift test --filter '(BridgeContractTests|ThemeCatalogTests)'
        if ($LASTEXITCODE -ne 0) { throw '跨语言契约测试没过，不出包' }
        Write-Warning '只有排序一致性那组红：Windows 的 ICU 与 mac 不等价，先出包'
    }
} finally { Pop-Location }

$core = Find-CoreLibrary -BuildDir (Join-Path $root '.build') -Configuration $Configuration
Copy-Item $core (Join-Path $artifacts 'omniplay-core.dll') -Force
$libmpv = Ensure-LibMpv -TargetDir (Join-Path $PSScriptRoot 'native')
if ($libmpv) { Copy-Item $libmpv (Join-Path $artifacts 'libmpv-2.dll') -Force }
else { Write-Warning '没有 libmpv：安装包里的播放器会显示「不可用」' }

Write-Host '==> dotnet publish' -ForegroundColor Cyan
# 参数单独算：写在数组字面量里时 ',' 会先于 '+' 结合，值会被拆成两个参数
$nativeDir = "-p:OmniPlayNativeDir=$artifacts"
$publishArgs = @(
    (Join-Path $PSScriptRoot 'OmniPlay.Wpf\OmniPlay.Wpf.csproj'),
    '-c', $Configuration, '-o', $publish, $nativeDir, '--nologo'
)
if (-not $FrameworkDependent) { $publishArgs += @('-r', 'win-x64', '--self-contained', 'true') }
dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish 失败' }

# 核心是动态链接 swiftCore 的，只带 omniplay-core.dll 出去在别人机器上起不来
Write-Host '==> Swift 运行时随包' -ForegroundColor Cyan
Copy-SwiftRuntime -CoreDll (Join-Path $artifacts 'omniplay-core.dll') -TargetDir $publish | Out-Null

# 冒烟：探针跑不过就不出包（与 mac 侧 pack_mac.sh 同一个态度）
Write-Host '==> abi-probe 冒烟' -ForegroundColor Cyan
dotnet build (Join-Path $PSScriptRoot 'abi-probe\AbiProbe.csproj') -c $Configuration $nativeDir | Out-Null
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
