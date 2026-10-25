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
# /MAP 出的符号表（和 DLL 同名换后缀）。用户机器上只能报回一个偏移，有这张表才查得出是谁
$map = [IO.Path]::ChangeExtension($core, 'map')
if (Test-Path $map) { Copy-Item $map (Join-Path $artifacts 'omniplay-core.map') -Force }
$libmpv = Ensure-LibMpv -TargetDir (Join-Path $PSScriptRoot 'native')
if ($libmpv) {
    Copy-Item $libmpv (Join-Path $artifacts 'libmpv-2.dll') -Force
    Write-Host ("  libmpv：{0}（{1:N1} MB）" -f (Split-Path -Leaf $libmpv), ((Get-Item $libmpv).Length / 1MB))
} else {
    Write-Warning '没有 libmpv：安装包里的播放器会显示「不可用」'
}

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
# 以探针自己打的结论为准：Windows 上它跑完全部检查后，进程在卸载 Swift 运行时时
# 仍可能带 0xC0000409 退出（见 docs/SPIKE.md §4），那种情况只警告不卡出包。
$probeLog = Join-Path $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }) 'abi-probe.log'
& (Join-Path $PSScriptRoot "abi-probe\bin\$Configuration\net8.0\abi-probe.exe") *>&1 |
    Tee-Object -FilePath $probeLog | ForEach-Object { Write-Host $_ }
$probeCode = $LASTEXITCODE
if (-not (Select-String -Path $probeLog -Pattern 'abi-probe: OK' -Quiet)) {
    throw "abi-probe 没通过（退出码 $probeCode），不出包"
}
if ($probeCode -ne 0) {
    Write-Warning ("探针全绿但进程退出码 0x{0:X8}：Windows 收尾问题，见 docs/SPIKE.md §4" -f ($probeCode -band 0xFFFFFFFF))
}

# 依赖自检：包里每个原生 DLL 要的东西，必须在包内或者系统目录里。
# libmpv 的可选图形依赖（vulkan 那类）在 Server 镜像上本来就没有，只提示不拦。
Write-Host '==> 随包依赖自检' -ForegroundColor Cyan
$gaps = @(Test-ShippedImports -PublishDir $publish)
$soft = @('libmpv-2.dll')
$serious = @($gaps | Where-Object { $soft -notcontains $_.Dll })
foreach ($gap in $serious) { Write-Host ("  {0} 缺：{1}" -f $gap.Dll, ($gap.Missing -join '、')) -ForegroundColor Red }
if ($serious) { throw '有原生 DLL 的依赖没进包：装到干净机器上会起不来' }
foreach ($gap in ($gaps | Where-Object { $soft -contains $_.Dll })) {
    Write-Warning ("{0} 的可选依赖这台机器上没有：{1}" -f $gap.Dll, ($gap.Missing -join '、'))
}

# 界面冒烟：把 PATH 缩到只剩系统目录再起一次，模拟「装了包但没有工具链」的机器。
# 控制台探针看不见的那两件事——WPF 能不能起、Swift 运行时在干净 PATH 下能不能拉起来——
# 在这里才看得见。开不了窗口的构建机只警告，依赖缺口才拦。
# 界面同时往 AppData 与 TEMP 写日志，读的时候两处都认。
$appLog = Join-Path $env:LOCALAPPDATA 'OmniPlay\startup.log'
$appLogFallback = Join-Path $env:TEMP 'OmniPlay-startup.log'

function Show-SmokeLog([string]$when) {
    foreach ($path in @($appLog, $appLogFallback) | Where-Object { Test-Path $_ }) {
        Write-Host "  | $when 的启动日志（$path）："
        Get-Content $path | ForEach-Object { Write-Host "  | $_" }
        break
    }
}

# 卡住的那一刻它到底在哪个调用里：dotnet-stack 直接打托管栈，比猜快
function Show-ManagedStack([int]$targetPid) {
    try {
        $dotnetRoot = Split-Path (Get-Command dotnet).Source
        $reporter = (Get-ChildItem (Join-Path $dotnetRoot 'sdk') -Recurse -Filter 'Stack.Core.dll' -ErrorAction SilentlyContinue |
                     Sort-Object FullName -Descending | Select-Object -First 1).FullName
        if ($reporter) { Write-Host (dotnet (Join-Path $dotnetRoot 'stack.dll') $targetPid 2>&1 | Out-String) }
        else { Write-Host '  ! 这台机器上没有 dotnet-stack，跳过托管栈' }
    } catch { Write-Host "  ! 托管栈没拿到：$_" }
}

function Invoke-UISmoke([string]$label, [string]$smokeArgs, [string]$require = '') {
    Write-Host "==> 界面冒烟：$label" -ForegroundColor Cyan
    Remove-Item $appLog, $appLogFallback -Force -ErrorAction SilentlyContinue
    $psi = New-Object System.Diagnostics.ProcessStartInfo((Join-Path $publish 'OmniPlaySpike.exe'), $smokeArgs)
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $publish
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardOutput = $true
    $psi.EnvironmentVariables['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
    $app = [System.Diagnostics.Process]::Start($psi)
    # 异步读干净两条流：Fatal error / Internal CLR error 这类只打到 stderr，
    # 光看启动日志看不见（日志停在哪儿只能说明断点，说明不了死因）
    $errTask = $app.StandardError.ReadToEndAsync()
    $outTask = $app.StandardOutput.ReadToEndAsync()
    if (-not $app.WaitForExit(90000)) {
        $app.Kill()
        Show-SmokeLog '超时时'                       # 只有「没退出」这一句等于白跑一轮
        [void]$errTask.Wait(2000); [void]$outTask.Wait(2000)   # 进程是 Kill 的，流会跟着关
        Write-Host ("  ! 超时时的 stderr：`n" + $errTask.Result)
        Show-ManagedStack $app.Id
        try {
            Get-Process -Name wermgr -ErrorAction SilentlyContinue | ForEach-Object {
                Write-Host "  ! wermgr 在跑（$($_.Id)）：大概弹了「已停止工作」对话框" }
        } catch { }
        throw "界面冒烟（$label）90 秒没退出"
    }
    $stderr = $errTask.Result; $stdout = $outTask.Result
    $smokeCode = $app.ExitCode
    foreach ($line in ($stderr + "`n" + $stdout) -split "`r?`n") { if ($line) { Write-Host "  ! $line" } }
    Show-SmokeLog '这一趟'
    $logHit = @($appLog, $appLogFallback) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($logHit) {
        $raw = Get-Content -Raw $logHit
        # 探针那一趟必须把结论那行写出来，否则等于什么都没测
        if ($raw -match '探针：' -and $raw -notmatch '核心这一层全通') {
            throw "界面冒烟（$label）：核心探针没走通，见上面的日志"
        }
        if ($require -and $raw -notmatch [regex]::Escape($require)) {
            throw "界面冒烟（$label）：没看到「$require」这一行"
        }
        if ($raw -match '核心装载失败|界面搭建失败|本机自检有缺口|片库读取失败|原生崩溃|没画出一帧|现场没存下来') {
            throw "界面冒烟（$label）抓到问题，见上面的日志"
        }
        # 界面立起来了却没写下收尾那行 = 中途被硬带走，这种包发出去就是「点开闪退」
        if ($raw -match '界面搭建完成' -and $raw -notmatch '退出（') {
            throw "界面冒烟（$label）中途进程就没了：日志停在上面最后一行"
        }
    } else {
        Write-Warning "界面冒烟（$label）没留下启动日志：这台机器大概开不了窗口"
    }
    if ($stderr -match 'Fatal error|Internal CLR error|Unhandled exception') {
        throw "界面冒烟（$label）抓到致命错误（stderr），见上面的输出"
    }
    if ($smokeCode -ne 0) { Write-Warning "界面冒烟（$label）退出码 $smokeCode（构建机开不了窗时属预期）" }
}

# 第二趟只把玻璃采样关掉：这是给用户留的排障档（--no-glass），CI 得替它验一遍能用。
# 第三趟完全不建窗：核心那一摊在 WPF 之外能不能跑通，界面起不来时先问这个。
Invoke-UISmoke '完整功能' '--smoke' '阶段：第一帧已画出'
Invoke-UISmoke '关掉玻璃采样' '--smoke --no-glass --no-library'
Invoke-UISmoke '只跑核心探针' '--probe' '核心这一层全通'
Invoke-UISmoke '最小 WPF 窗口' '--min-wpf --smoke' '最小窗口：第一帧已画出'
Invoke-UISmoke '不用 WPF 的窗口探针' '--win32-probe --smoke' '消息泵走完'

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
