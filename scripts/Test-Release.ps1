[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SqlConnection
)
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Release acceptance requires Windows (LocalDB and the checked-in browser host fixture are Windows-tested).' }
if ([string]::IsNullOrWhiteSpace($SqlConnection)) { throw '-SqlConnection is required; supply a disposable SQL Server test instance.' }
$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or [version]$sdk -lt [version]'10.0') { throw '.NET 10 SDK or later is required for the package consumer acceptance host.' }
$browserRoot = if ($env:PLAYWRIGHT_BROWSERS_PATH) { $env:PLAYWRIGHT_BROWSERS_PATH } else { Join-Path $env:LOCALAPPDATA 'ms-playwright' }
if (-not (Test-Path -LiteralPath $browserRoot -PathType Container) -or
    -not (Get-ChildItem -LiteralPath $browserRoot -Directory -Filter 'chromium-*' | Where-Object Name -notlike '*headless_shell*' | Select-Object -First 1)) {
    throw "Playwright Chromium is required. Install the browser for Microsoft.Playwright 1.63.0; checked '$browserRoot'."
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('qab-release-' + [guid]::NewGuid().ToString('N'))
$artifacts = Join-Path $scratch 'artifacts'
$results = Join-Path $scratch 'results'
$feed = Join-Path $scratch 'feed'
$version = '0.1.0-ci.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmss') + '.' + (Get-Random -Minimum 1000 -Maximum 9999)
$originalSql = $env:QAB_TEST_SQLSERVER
$originalPlan = $env:QAB_REPORT_BENCHMARK_PLAN
$originalRepositoryRoot = $env:QAB_REPOSITORY_ROOT
try {
    New-Item -ItemType Directory -Path $results | Out-Null
    $env:QAB_REPOSITORY_ROOT = $root
    $env:QAB_TEST_SQLSERVER = $SqlConnection
    $env:QAB_REPORT_BENCHMARK_PLAN = Join-Path $results 'report-page-actual-plan.sqlplan'
    $testProjects = @(
        @{ Name = 'Application'; Path = 'tests/QuestionAppBuilder.Application.Tests/QuestionAppBuilder.Application.Tests.csproj' },
        @{ Name = 'Editor'; Path = 'tests/QuestionAppBuilder.Editor.Tests/QuestionAppBuilder.Editor.Tests.csproj' },
        @{ Name = 'Infrastructure'; Path = 'tests/QuestionAppBuilder.Infrastructure.Tests/QuestionAppBuilder.Infrastructure.Tests.csproj'; Filter = 'Category!=Scale' },
        @{ Name = 'Reports'; Path = 'tests/QuestionAppBuilder.Reports.Tests/QuestionAppBuilder.Reports.Tests.csproj' },
        @{ Name = 'Runtime'; Path = 'tests/QuestionAppBuilder.Runtime.Tests/QuestionAppBuilder.Runtime.Tests.csproj' },
        @{ Name = 'Browser'; Path = 'tests/QuestionAppBuilder.Browser.Tests/QuestionAppBuilder.Browser.Tests.csproj' }
    )
    foreach ($testProject in $testProjects) {
        $arguments = @('test', (Join-Path $root $testProject.Path), '--artifacts-path', $artifacts, '--results-directory', $results,
            '--logger', "trx;LogFileName=$($testProject.Name).trx", '--verbosity', 'minimal')
        if ($testProject.Filter) { $arguments += @('--filter', $testProject.Filter) }
        Write-Output "Running $($testProject.Name) tests sequentially..."
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw "$($testProject.Name) test suite failed (exit $LASTEXITCODE); inspect $results." }
    }

    $normalTrx = @(Get-ChildItem -LiteralPath $results -Filter '*.trx' | Where-Object Name -ne 'ReportingScale.trx')
    $skipped = @()
    foreach ($trx in $normalTrx) {
        [xml]$xml = Get-Content -LiteralPath $trx.FullName -Raw
        $skipped += @($xml.SelectNodes("//*[local-name()='UnitTestResult' and @outcome='NotExecuted']"))
    }
    if ($skipped.Count -ne 1 -or $skipped[0].GetAttribute('testName') -notlike '*Installed_packages_render_assets_and_views_under_custom_prefix*') {
        throw "Expected only the package-consumer browser fact to skip before the package host starts; observed $($skipped.Count) skipped tests. Inspect $results."
    }

    Write-Output 'Running the 10,000-submission reporting scale benchmark sequentially after ordinary suites...'
    & dotnet test (Join-Path $root 'tests/QuestionAppBuilder.Infrastructure.Tests/QuestionAppBuilder.Infrastructure.Tests.csproj') --no-build --artifacts-path $artifacts --results-directory $results --logger 'trx;LogFileName=ReportingScale.trx' --filter 'FullyQualifiedName~Reporting_scale_benchmark_uses_ten_thousand_submissions_and_fifty_questions' --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Reporting scale benchmark failed (exit $LASTEXITCODE); inspect $results." }
    if (-not (Test-Path -LiteralPath $env:QAB_REPORT_BENCHMARK_PLAN)) { throw 'Reporting scale test did not produce its SQL Server actual-plan artifact.' }

    & (Join-Path $PSScriptRoot 'Test-Packages.ps1') -FeedPath $feed -Version $version -SqlConnection $SqlConnection -KeepEvidence
    if ($LASTEXITCODE -ne 0) { throw "Installed-package acceptance failed (exit $LASTEXITCODE)." }
    Write-Output "Release acceptance passed. Package version: $version. Sequential test TRX and reporting plan: $results. Build artifacts: $artifacts."
} finally {
    if ($null -eq $originalSql) { Remove-Item Env:QAB_TEST_SQLSERVER -ErrorAction SilentlyContinue } else { $env:QAB_TEST_SQLSERVER = $originalSql }
    if ($null -eq $originalPlan) { Remove-Item Env:QAB_REPORT_BENCHMARK_PLAN -ErrorAction SilentlyContinue } else { $env:QAB_REPORT_BENCHMARK_PLAN = $originalPlan }
    if ($null -eq $originalRepositoryRoot) { Remove-Item Env:QAB_REPOSITORY_ROOT -ErrorAction SilentlyContinue } else { $env:QAB_REPOSITORY_ROOT = $originalRepositoryRoot }
}
