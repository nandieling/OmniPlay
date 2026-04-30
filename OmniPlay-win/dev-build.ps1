[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$SkipTests,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$WindowsRoot = Split-Path -Parent $PSCommandPath
$RepoRoot = Split-Path -Parent $WindowsRoot
$SolutionFile = Join-Path $WindowsRoot 'OmniPlay.Windows.slnx'

$env:DOTNET_CLI_HOME = Join-Path $RepoRoot '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'

# Solution builds are unstable under the CLI's default multi-node scheduling in this workspace.
$buildArgs = @(
    'build'
    $SolutionFile
    '-c'
    $Configuration
    '-m:1'
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

if ($SkipTests)
{
    exit 0
}

$testArgs = @(
    'test'
    $SolutionFile
    '-c'
    $Configuration
    '-m:1'
    '-v'
    'minimal'
)

if ($NoRestore)
{
    $testArgs += '--no-restore'
}

& dotnet @testArgs
exit $LASTEXITCODE
