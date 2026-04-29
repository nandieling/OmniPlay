[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$AppRoot,
    [string]$PlayFile,
    [string]$OverlayPlayFile,
    [double]$CloseAfter,
    [switch]$SkipBuild,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

if ($PlayFile -and $OverlayPlayFile)
{
    throw 'Use either -PlayFile or -OverlayPlayFile, not both.'
}

function Resolve-AbsolutePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ([System.IO.Path]::IsPathRooted($Value))
    {
        return [System.IO.Path]::GetFullPath($Value)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $RepoRoot $Value))
}

$WindowsRoot = Split-Path -Parent $PSCommandPath
$RepoRoot = Split-Path -Parent $WindowsRoot
$DesktopProject = Join-Path $WindowsRoot 'src\OmniPlay.Desktop\OmniPlay.Desktop.csproj'

$env:DOTNET_CLI_HOME = Join-Path $RepoRoot '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

if ($AppRoot)
{
    $env:OMNIPLAY_APP_ROOT = Resolve-AbsolutePath $AppRoot
}
else
{
    Remove-Item Env:\OMNIPLAY_APP_ROOT -ErrorAction SilentlyContinue
}

Write-Host "Desktop project: $DesktopProject"
if ($env:OMNIPLAY_APP_ROOT)
{
    Write-Host "OMNIPLAY_APP_ROOT: $env:OMNIPLAY_APP_ROOT"
}
else
{
    Write-Host 'OMNIPLAY_APP_ROOT: <auto>'
}

if (-not $SkipBuild)
{
    $buildArgs = @(
        'build'
        $DesktopProject
        '-c'
        $Configuration
        '-p:BuildInParallel=false'
    )

    if ($NoRestore)
    {
        $buildArgs += '--no-restore'
    }

    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0)
    {
        exit $LASTEXITCODE
    }
}

$runArgs = @(
    'run'
    '--project'
    $DesktopProject
    '-c'
    $Configuration
    '--no-build'
    '--no-restore'
)

$appArgs = @()

if ($PlayFile)
{
    $appArgs += '--play-file'
    $appArgs += Resolve-AbsolutePath $PlayFile
}

if ($OverlayPlayFile)
{
    $appArgs += '--overlay-play-file'
    $appArgs += Resolve-AbsolutePath $OverlayPlayFile
}

if ($PSBoundParameters.ContainsKey('CloseAfter'))
{
    $appArgs += '--close-after'
    $appArgs += $CloseAfter.ToString([System.Globalization.CultureInfo]::InvariantCulture)
}

if ($appArgs.Count -gt 0)
{
    $runArgs += '--'
    $runArgs += $appArgs
}

& dotnet @runArgs
exit $LASTEXITCODE
