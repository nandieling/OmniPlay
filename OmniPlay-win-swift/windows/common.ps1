# 三个脚本共用的几件事：找核心动态库、备 libmpv、找 Inno Setup。

function Find-CoreLibrary {
    param([Parameter(Mandatory = $true)][string]$BuildDir,
          [Parameter(Mandatory = $true)][string]$Configuration)

    # SwiftPM 的产物目录随工具链版本变过，两种布局都试一遍再兜底全盘找
    $candidates = @(
        (Join-Path $BuildDir "$Configuration\omniplay-core.dll"),
        (Join-Path $BuildDir "out\Products\$Configuration\omniplay-core.dll")
    )
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return (Resolve-Path $candidate).Path }
    }
    $hit = Get-ChildItem -Path $BuildDir -Recurse -Filter 'omniplay-core.dll' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*$Configuration*" } | Select-Object -First 1
    if (-not $hit) { throw "没找到 omniplay-core.dll，先跑 swift build -c $Configuration" }
    return $hit.FullName
}

function Ensure-LibMpv {
    param([Parameter(Mandatory = $true)][string]$TargetDir,
          [string]$Url = $env:LIBMPV_URL)

    $installed = Join-Path $TargetDir 'libmpv-2.dll'
    if (Test-Path $installed) { return $installed }
    if (-not $Url) { return $null }

    try {
        $archive = Join-Path $env:TEMP 'omniplay-libmpv-archive'
        Invoke-WebRequest -Uri $Url -OutFile $archive
        $sevenZip = Join-Path ${env:ProgramFiles} '7-Zip\7z.exe'
        if (-not (Test-Path $sevenZip)) { throw '需要 7-Zip 才能解 libmpv 压缩包' }
        $extract = Join-Path $env:TEMP 'omniplay-libmpv-extract'
        if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
        & $sevenZip x "-o$extract" $archive '-r' -y | Out-Null
        $dll = Get-ChildItem -Path $extract -Recurse -Filter 'libmpv-2.dll' | Select-Object -First 1
        if (-not $dll) { throw '压缩包里没找到 libmpv-2.dll' }
        Copy-Item $dll.FullName $installed
        return $installed
    } catch {
        Write-Warning "libmpv 没取到（$($_.Exception.Message)）：播放器会以「不可用」启动"
        return $null
    }
}

function Find-InnoSetup {
    $onPath = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    foreach ($candidate in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $candidate) { return $candidate }
    }
    try {
        choco install innosetup -y --no-progress | Out-Null
        if (Test-Path "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") {
            return "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
        }
    } catch {
        Write-Warning "装 Inno Setup 失败：$($_.Exception.Message)"
    }
    return $null
}
