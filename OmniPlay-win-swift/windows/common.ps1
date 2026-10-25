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
        New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
        $archive = Join-Path $env:TEMP 'omniplay-libmpv-archive'
        foreach ($one in ($Url -split ';' | Where-Object { $_ })) {
            Invoke-WebRequest -Uri $one.Trim() -OutFile $archive -UserAgent 'OmniPlay-Packaging'
            # 镜像有时会回一个页面而不是压缩包，先看魔数，别把 HTML 喂给 7z
            $head = Get-Content -LiteralPath $archive -AsByteStream -TotalCount 6
            if (-not (($head[0] -eq 0x37) -and ($head[1] -eq 0x7A) -and ($head[2] -eq 0xBC) -and
                      ($head[3] -eq 0xAF) -and ($head[4] -eq 0x27) -and ($head[5] -eq 0x1C))) {
                Write-Warning "拿到的不是 7z（前 6 字节 $($head -join ' ')），换下一个地址"
                continue
            }
            $sevenZip = Join-Path ${env:ProgramFiles} '7-Zip\7z.exe'
            if (-not (Test-Path $sevenZip)) { throw '需要 7-Zip 才能解 libmpv 压缩包' }
            $extract = Join-Path $env:TEMP 'omniplay-libmpv-extract'
            if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
            & $sevenZip x "-o$extract" $archive '-r' -y | Out-Null
            $dll = Get-ChildItem -Path $extract -Recurse -Filter 'libmpv-2.dll' | Select-Object -First 1
            if (-not $dll) { throw "压缩包里没找到 libmpv-2.dll（$one）" }
            Copy-Item $dll.FullName $installed
            return $installed
        }
        Write-Warning 'libmpv 地址全部不可用：播放器会以「不可用」启动'
        return $null
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

# Swift 在 Windows 上永远动态链接 swiftCore 之类，装到用户机器上必须把这几个 DLL 一起带上。
# Swift 运行时 DLL 在 Windows 上的位置跟编译器不在一起：编译器在 Toolchains\<版本>\usr\bin，
# 运行时在 SDKROOT\usr\bin。两处都试，再退回扫 PATH（安装器把运行时的目录加进去了）。
function Find-SwiftRuntimeDir {
    $candidates = @()
    if ($env:SDKROOT) { $candidates += (Join-Path $env:SDKROOT 'usr\bin') }
    $cmd = Get-Command swift.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        $toolchain = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $cmd.Source))
        $candidates += (Join-Path $toolchain 'usr\bin')
    }
    $candidates += ($env:Path -split ';' | Where-Object { $_ })
    foreach ($candidate in $candidates) {
        $probe = Join-Path $candidate 'swiftCore.dll'
        if (Test-Path -LiteralPath $probe) { return (Split-Path -Parent $probe) }
    }
    return $null
}

# 读 PE 导入表，够回答「这个 DLL 还依赖谁」就行，不做完整解析。
function Get-PeImageImports {
    param([Parameter(Mandatory = $true)][string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x40 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0x5A4D) { return @() }
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    if ($pe -le 0 -or ($pe + 24) -gt $bytes.Length) { return @() }
    $sizeOfOptional = [BitConverter]::ToUInt16($bytes, $pe + 20)
    $sections = [BitConverter]::ToUInt16($bytes, $pe + 6)
    $optional = $pe + 24
    $magic = [BitConverter]::ToUInt16($bytes, $optional)
    # 数据目录起点：PE32+ 在可选头 +112，PE32 在 +96；导入表是第 2 项，每项 8 字节
    $directory = $optional + $(if ($magic -eq 0x20B) { 112 } else { 96 }) + 8
    $importRva = [BitConverter]::ToUInt32($bytes, $directory)
    if ($importRva -eq 0) { return @() }
    $sectionTable = $optional + $sizeOfOptional

    $offsetOf = {
        param([uint32]$Rva)
        for ($i = 0; $i -lt $sections; $i++) {
            $row = $sectionTable + ($i * 40)
            $virtual = [BitConverter]::ToUInt32($bytes, $row + 12)
            $virtualSize = [BitConverter]::ToUInt32($bytes, $row + 8)
            $rawSize = [BitConverter]::ToUInt32($bytes, $row + 16)
            $raw = [BitConverter]::ToUInt32($bytes, $row + 20)
            if ($Rva -ge $virtual -and $Rva -lt ($virtual + [Math]::Max($virtualSize, $rawSize))) {
                return [int]($raw + ($Rva - $virtual))
            }
        }
        return -1
    }

    $names = New-Object System.Collections.Generic.List[string]
    $descriptor = & $offsetOf $importRva
    while (($descriptor -ge 0) -and ($descriptor + 20) -le $bytes.Length) {
        $nameRva = [BitConverter]::ToUInt32($bytes, $descriptor + 12)
        if ($nameRva -eq 0) { break }
        $at = & $offsetOf $nameRva
        if ($at -lt 0 -or $at -ge $bytes.Length) { break }
        $end = $at
        while (($end -lt $bytes.Length) -and ($bytes[$end] -ne 0)) { $end++ }
        $names.Add([System.Text.Encoding]::ASCII.GetString($bytes, $at, $end - $at))
        $descriptor += 20
    }
    return $names
}

# 顺着核心 DLL 的导入，把工具链 usr\bin 里的 Swift 运行时拷进发布目录。
# MSVC 运行库（vcruntime/msvcp/concrt）单独放 redist\：摆进应用目录会盖住机器上本来
# 更新的那份，而 WPF 的原生组件也要吃这套；宿主只在装载失败时才把它补进搜索路径。
function Copy-SwiftRuntime {
    param([Parameter(Mandatory = $true)][string]$CoreDll,
          [Parameter(Mandatory = $true)][string]$TargetDir)

    $bin = Find-SwiftRuntimeDir
    if (-not $bin) {
        Write-Warning '没找到 Swift 运行时目录（swiftCore.dll）：运行时没进包，换台没装 Swift 的机器会起不来'
        return @()
    }
    $redist = Join-Path $TargetDir 'redist'
    $queue = New-Object System.Collections.Generic.Queue[string]
    foreach ($name in (Get-PeImageImports -Path $CoreDll)) { $queue.Enqueue($name) }

    $all = New-Object System.Collections.Generic.List[string]
    $runtime = New-Object System.Collections.Generic.List[string]
    $bytes = 0L
    while ($queue.Count -gt 0) {
        $name = $queue.Dequeue()
        if (($all -contains $name) -or ($runtime -contains $name)) { continue }
        # MSVC CRT：用户机器上多半已有一份，工具链里那份未必更新，所以只进 redist\ 备着
        $isCrt = $name -match '^(vcruntime|msvcp|concrt)'
        $source = Join-Path $bin $name                       # Swift 运行时：工具链里那份
        if (-not (Test-Path -LiteralPath $source)) {
            if (-not $isCrt) { continue }
            $source = Join-Path (Join-Path $env:SystemRoot 'System32') $name
            if (-not (Test-Path -LiteralPath $source)) { continue }
        }
        $isRuntime = -not $isCrt
        if ($isRuntime) {
            $dest = Join-Path $TargetDir $name
        } else {
            New-Item -ItemType Directory -Force -Path $redist | Out-Null
            $dest = Join-Path $redist $name
        }
        try {
            $item = Get-Item -LiteralPath $source
            Copy-Item -LiteralPath $source -Destination $dest -Force
            $all.Add($name)
            $bytes += $item.Length
            if ($isRuntime) { $runtime.Add($name) }
            # CRT 也要往下走一层：MSVCP140 还要吃 VCRUNTIME140_1，少一个就是白折腾
            foreach ($next in (Get-PeImageImports -Path $source)) { $queue.Enqueue($next) }
        } catch {
            Write-Warning "拷 $name 时出岔子：$($_.Exception.Message)"
        }
    }

    if ($runtime.Count -eq 0) {
        Write-Warning '核心 DLL 的导入表里没解析出任何工具链 DLL：运行时可能没进包，装到别的机器上试试'
    } else {
        Write-Host ("  Swift 运行时：{0} 个 DLL，{1:N1} MB；MSVC 运行库 {2} 个放 redist\" -f
            $runtime.Count, ($bytes / 1MB), ($all.Count - $runtime.Count))
    }
    return $all
}

# 依赖自检：包里每个原生 DLL 的导入，要么也在包里，要么在 System32，要么是 api-ms-/ext-ms-
# 契约。构建机 PATH 里整条工具链都在，看不出真缺口——装到干净机器上的闪退就是这么来的。
function Test-ShippedImports {
    param([Parameter(Mandatory = $true)][string]$PublishDir)

    $system32 = Join-Path $env:SystemRoot 'System32'
    $shipped = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -Filter '*.dll' -File |
        ForEach-Object { $_.Name.ToLowerInvariant() })
    $gaps = @()
    foreach ($dll in Get-ChildItem -LiteralPath $PublishDir -Recurse -Filter '*.dll' -File) {
        $imports = Get-PeImageImports -Path $dll.FullName
        # 托管程序集那身导入表是给 CLR 看的（清一色 mscoree.dll），系统装载器不参与
        if ($imports -contains 'mscoree.dll') { continue }
        $missing = @($imports | Where-Object {
            ($_.StartsWith('api-ms-') -or $_.StartsWith('ext-ms-')) -eq $false -and
            ($shipped -notcontains $_.ToLowerInvariant()) -and
            (-not (Test-Path -LiteralPath (Join-Path $system32 $_)))
        } | Sort-Object -Unique)
        if ($missing.Count -gt 0) { $gaps += [pscustomobject]@{ Dll = $dll.Name; Missing = $missing } }
    }
    return $gaps
}
