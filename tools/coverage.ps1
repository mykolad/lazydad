<#
.SYNOPSIS
    Runs the tests with coverage and enforces the minimum line coverage.
    CI runs exactly this script, so a local run reproduces the CI gate.
.EXAMPLE
    ./tools/coverage.ps1
    ./tools/coverage.ps1 -NoBuild   # as CI runs it, after a Release build
#>
param(
    # Release by default so the numbers match CI (Debug builds have more coverable lines).
    [string] $Configuration = 'Release',
    [switch] $NoBuild,
    # Ratchet this up as tests are added; never lower it to get a PR through.
    [double] $MinLineCoverage = 85
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

Remove-Item TestResults, coverage -Recurse -Force -ErrorAction SilentlyContinue

dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }

# Only the unit test project: the smoke tests (tests/LazyDad.SmokeTests) target a deployed app
# and are run by CD. Running the solution would also start the smoke project, and both projects
# would write the same results.trx, so one could overwrite the other.
$testArgs = @(
    'test', 'tests/LazyDad.Tests/LazyDad.Tests.csproj', '-c', $Configuration,
    '--collect', 'XPlat Code Coverage',
    '--settings', 'tests/LazyDad.Tests/coverage.runsettings',
    '--results-directory', 'TestResults',
    '--logger', 'trx;LogFileName=results.trx'
)
if ($NoBuild) { $testArgs += '--no-build' }

dotnet @testArgs
$testExitCode = $LASTEXITCODE

# Report even when tests fail, so the partial coverage is still visible.
dotnet tool run reportgenerator '-reports:TestResults/*/coverage.cobertura.xml' '-targetdir:coverage' '-reporttypes:MarkdownSummaryGithub;JsonSummary;Html'
if ($LASTEXITCODE -ne 0) { throw 'Coverage report generation failed.' }

if ($testExitCode -ne 0) { throw 'Tests failed.' }

$lineCoverage = (Get-Content coverage/Summary.json -Raw | ConvertFrom-Json).summary.linecoverage
Write-Host "Line coverage: $lineCoverage% (minimum $MinLineCoverage%). Report: coverage/index.html"

if ($lineCoverage -lt $MinLineCoverage) {
    $message = "Line coverage $lineCoverage% is below the required $MinLineCoverage%."
    if ($env:GITHUB_ACTIONS) { Write-Host "::error title=Coverage too low::$message" }
    throw $message
}
