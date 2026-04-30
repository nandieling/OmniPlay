[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$RuntimeIdentifier = 'win-x64',

    [string]$OutputDirectory,

    [switch]$FrameworkDependentInstaller,

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$WindowsRoot = Split-Path -Parent $PSCommandPath
$RepoRoot = Split-Path -Parent $WindowsRoot
$DesktopProject = Join-Path $WindowsRoot 'src\OmniPlay.Desktop\OmniPlay.Desktop.csproj'
$SetupProject = Join-Path $WindowsRoot 'installer\OmniPlay.Setup\OmniPlay.Setup.csproj'
$IconPng = Join-Path $WindowsRoot '1.png'
$AppIcon = Join-Path $WindowsRoot 'src\OmniPlay.Desktop\app.ico'
$PackageRoot = Join-Path $WindowsRoot 'tmp\package'
$AppStage = Join-Path $PackageRoot 'app'
$PayloadDir = Join-Path $PackageRoot 'payload'
$PayloadZip = Join-Path $PayloadDir 'OmniPlayPayload.zip'
$SetupStage = Join-Path $PackageRoot 'setup'
$DistDir = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $WindowsRoot 'dist' } else { $OutputDirectory }
$SetupExeName = "$([char]0x89C8)$([char]0x5F71)-OmniPlay-x64-setup.exe"
$SetupExe = Join-Path $DistDir $SetupExeName

function Get-FullPath([string]$Path)
{
    return [System.IO.Path]::GetFullPath($Path)
}

function Assert-UnderRoot([string]$Path, [string]$Root)
{
    $fullPath = Get-FullPath $Path
    $fullRoot = Get-FullPath $Root
    if (-not $fullRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar))
    {
        $fullRoot += [System.IO.Path]::DirectorySeparatorChar
    }

    if (-not $fullPath.StartsWith($fullRoot, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to operate outside workspace: $fullPath"
    }
}

function Remove-SafeDirectory([string]$Path)
{
    if (-not (Test-Path -LiteralPath $Path))
    {
        return
    }

    Assert-UnderRoot $Path $WindowsRoot
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Remove-SafeFile([string]$Path)
{
    if (-not (Test-Path -LiteralPath $Path))
    {
        return
    }

    Assert-UnderRoot $Path $WindowsRoot
    Remove-Item -LiteralPath $Path -Force
}

function Invoke-DotNet([string[]]$Arguments, [string]$FailureMessage)
{
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw $FailureMessage
    }
}

function Invoke-WithRetry([scriptblock]$Action, [string]$FailureMessage, [int]$MaxAttempts = 6)
{
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++)
    {
        try
        {
            & $Action
            return
        }
        catch
        {
            if ($attempt -eq $MaxAttempts)
            {
                throw "$FailureMessage $($_.Exception.Message)"
            }

            Start-Sleep -Seconds ([Math]::Min(10, $attempt * 2))
        }
    }
}

function Convert-PngToIcon([string]$SourcePng, [string]$DestinationIco)
{
    if (-not (Test-Path -LiteralPath $SourcePng))
    {
        throw "Icon source not found: $SourcePng"
    }

    Add-Type -AssemblyName System.Drawing

    $sizes = @(256, 128, 64, 48, 32, 16)
    $source = [System.Drawing.Image]::FromFile($SourcePng)
    try
    {
        $entries = @()
        foreach ($size in $sizes)
        {
            $bitmap = New-Object System.Drawing.Bitmap -ArgumentList $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try
            {
                $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
                try
                {
                    $graphics.Clear([System.Drawing.Color]::Transparent)
                    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality

                    $scale = [Math]::Min($size / [double]$source.Width, $size / [double]$source.Height)
                    $width = [int][Math]::Round($source.Width * $scale)
                    $height = [int][Math]::Round($source.Height * $scale)
                    $x = [int][Math]::Floor(($size - $width) / 2)
                    $y = [int][Math]::Floor(($size - $height) / 2)

                    $graphics.DrawImage($source, $x, $y, $width, $height)
                }
                finally
                {
                    $graphics.Dispose()
                }

                $stream = New-Object System.IO.MemoryStream
                try
                {
                    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                    $entries += [pscustomobject]@{
                        Size = $size
                        Bytes = $stream.ToArray()
                    }
                }
                finally
                {
                    $stream.Dispose()
                }
            }
            finally
            {
                $bitmap.Dispose()
            }
        }

        $destinationDirectory = Split-Path -Parent $DestinationIco
        if (-not [string]::IsNullOrWhiteSpace($destinationDirectory))
        {
            New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
        }

        $file = [System.IO.File]::Open($DestinationIco, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try
        {
            $writer = New-Object System.IO.BinaryWriter -ArgumentList $file
            try
            {
                $writer.Write([uint16]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]$entries.Count)

                $offset = 6 + (16 * $entries.Count)
                foreach ($entry in $entries)
                {
                    $dimension = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
                    $writer.Write([byte]$dimension)
                    $writer.Write([byte]$dimension)
                    $writer.Write([byte]0)
                    $writer.Write([byte]0)
                    $writer.Write([uint16]1)
                    $writer.Write([uint16]32)
                    $writer.Write([uint32]$entry.Bytes.Length)
                    $writer.Write([uint32]$offset)
                    $offset += $entry.Bytes.Length
                }

                foreach ($entry in $entries)
                {
                    $writer.Write([byte[]]$entry.Bytes)
                }
            }
            finally
            {
                $writer.Dispose()
            }
        }
        finally
        {
            $file.Dispose()
        }
    }
    finally
    {
        $source.Dispose()
    }
}

$env:DOTNET_CLI_HOME = Join-Path $WindowsRoot '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

Invoke-WithRetry {
    Remove-SafeDirectory $PackageRoot
} 'Failed to clean package staging directory.'
New-Item -ItemType Directory -Force -Path $AppStage, $PayloadDir, $SetupStage, $DistDir | Out-Null
Convert-PngToIcon $IconPng $AppIcon

$publishAppArgs = @(
    'publish'
    $DesktopProject
    '-c'
    $Configuration
    '-r'
    $RuntimeIdentifier
    '--self-contained'
    'true'
    '-o'
    $AppStage
    '-p:PublishSingleFile=false'
    '-p:NuGetAudit=false'
)

if ($NoRestore)
{
    $publishAppArgs += '--no-restore'
}

Invoke-DotNet $publishAppArgs 'Failed to publish OmniPlay desktop app.'

Get-ChildItem -Path $AppStage -Recurse -Filter '*.pdb' | Remove-Item -Force

if (-not (Test-Path -LiteralPath (Join-Path $AppStage 'OmniPlay.Desktop.exe')))
{
    throw 'Published app is missing OmniPlay.Desktop.exe.'
}

Remove-SafeFile $PayloadZip
Invoke-WithRetry {
    Remove-SafeFile $PayloadZip
    Compress-Archive -Path (Join-Path $AppStage '*') -DestinationPath $PayloadZip -CompressionLevel Optimal -Force
} 'Failed to create setup payload zip.'

$setupPublishArgs = @(
    'publish'
    $SetupProject
    '-c'
    $Configuration
    '-r'
    $RuntimeIdentifier
    '-p:PublishSingleFile=true'
    '-p:EnableCompressionInSingleFile=true'
    '-p:IncludeNativeLibrariesForSelfExtract=true'
    '-p:NuGetAudit=false'
    '-o'
    $SetupStage
)

if ($NoRestore)
{
    $setupPublishArgs += '--no-restore'
}

if ($FrameworkDependentInstaller)
{
    $setupPublishArgs += @('--self-contained', 'false')
    Invoke-DotNet $setupPublishArgs 'Failed to publish framework-dependent setup.exe.'
}
else
{
    $selfContainedArgs = $setupPublishArgs + @('--self-contained', 'true')
    Invoke-WithRetry {
        Invoke-DotNet $selfContainedArgs 'Failed to publish self-contained setup.exe.'
    } 'Failed to publish self-contained setup.exe.' 3
}

$builtSetupExe = Join-Path $SetupStage 'setup.exe'
if (-not (Test-Path -LiteralPath $builtSetupExe))
{
    throw 'setup.exe was not produced.'
}

Remove-SafeFile $SetupExe
Copy-Item -LiteralPath $builtSetupExe -Destination $SetupExe -Force

& $SetupExe /verify /quiet
if ($LASTEXITCODE -ne 0)
{
    throw 'Generated setup.exe failed payload verification.'
}

$setupInfo = Get-Item -LiteralPath $SetupExe
Write-Host "Created setup package: $($setupInfo.FullName)"
Write-Host "Size: $([Math]::Round($setupInfo.Length / 1MB, 2)) MB"
