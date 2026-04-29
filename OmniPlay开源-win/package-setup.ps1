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
$PackageRoot = Join-Path $WindowsRoot 'tmp\package'
$AppStage = Join-Path $PackageRoot 'app'
$PayloadDir = Join-Path $PackageRoot 'payload'
$PayloadZip = Join-Path $PayloadDir 'OmniPlayPayload.zip'
$SetupStage = Join-Path $PackageRoot 'setup'
$DistDir = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $WindowsRoot 'dist' } else { $OutputDirectory }
$SetupExe = Join-Path $DistDir 'setup.exe'

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

$env:DOTNET_CLI_HOME = Join-Path $RepoRoot '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

Remove-SafeDirectory $PackageRoot
New-Item -ItemType Directory -Force -Path $AppStage, $PayloadDir, $SetupStage, $DistDir | Out-Null

$publishAppArgs = @(
    'publish'
    $DesktopProject
    '-c'
    $Configuration
    '--self-contained'
    'false'
    '-o'
    $AppStage
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
Compress-Archive -Path (Join-Path $AppStage '*') -DestinationPath $PayloadZip -CompressionLevel Optimal -Force

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
    & dotnet @selfContainedArgs
    if ($LASTEXITCODE -ne 0)
    {
        Write-Warning 'Self-contained setup.exe publish failed. Falling back to framework-dependent single-file setup.exe.'
        $frameworkDependentArgs = $setupPublishArgs + @('--self-contained', 'false')
        Invoke-DotNet $frameworkDependentArgs 'Failed to publish setup.exe.'
    }
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
