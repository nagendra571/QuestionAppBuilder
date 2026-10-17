[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $FeedPath,
    [Parameter(Mandatory)] [string] $Version
)
$ErrorActionPreference = 'Stop'

if (-not [IO.Path]::IsPathFullyQualified($FeedPath)) { throw '-FeedPath must be an absolute path.' }
if ($Version -notmatch '^\d+\.\d+\.\d+-[A-Za-z0-9][A-Za-z0-9.-]*$') { throw '-Version must be a unique prerelease SemVer.' }
$feed = [IO.Path]::GetFullPath($FeedPath)
if (Test-Path -LiteralPath $feed) {
    if ((Get-ChildItem -LiteralPath $feed -Force | Measure-Object).Count -ne 0) { throw "Feed must be unique and empty: $feed" }
} else { New-Item -ItemType Directory -Path $feed | Out-Null }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("qab-package-" + [guid]::NewGuid().ToString('N'))
$artifacts = Join-Path $scratch 'artifacts'
$nuget = Join-Path $scratch 'packages'
$nugetConfig = Join-Path $scratch 'NuGet.Config'
$stdout = Join-Path $scratch 'host.stdout.log'
$stderr = Join-Path $scratch 'host.stderr.log'
$hostProcess = $null
$originalEnvironment = $env:ASPNETCORE_ENVIRONMENT
$originalPackageHostUrl = $env:QAB_PACKAGE_HOST_URL
$originalRepositoryRoot = $env:QAB_REPOSITORY_ROOT
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    $env:QAB_REPOSITORY_ROOT = $root
    $projects = @(
        'src/QuestionAppBuilder.Domain/QuestionAppBuilder.Domain.csproj',
        'src/QuestionAppBuilder.Application/QuestionAppBuilder.Application.csproj',
        'src/QuestionAppBuilder.Infrastructure/QuestionAppBuilder.Infrastructure.csproj',
        'src/QuestionAppBuilder.Core/QuestionAppBuilder.Core.csproj',
        'src/QuestionAppBuilder.Editor/QuestionAppBuilder.Editor.csproj',
        'src/QuestionAppBuilder.Reports/QuestionAppBuilder.Reports.csproj'
    )
    foreach ($project in $projects) {
        & dotnet pack (Join-Path $root $project) --configuration Release --artifacts-path $artifacts --output $feed "-p:PackageVersion=$Version" --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw "Packing failed for $project (exit $LASTEXITCODE)." }
    }
    $expected = @('QuestionAppBuilder.Domain','QuestionAppBuilder.Application','QuestionAppBuilder.Infrastructure','QuestionAppBuilder.Core','QuestionAppBuilder.Editor','QuestionAppBuilder.Reports')
    $actual = @(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' | ForEach-Object { $_.BaseName -replace '\.\d+\.\d+\.\d+-.*$','' })
    foreach ($id in $expected) { if ($actual -notcontains $id) { throw "Package '$id' was not produced." } }

    $feedXml = [Security.SecurityElement]::Escape($feed)
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="private-feed" value="$feedXml"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
"@ | Set-Content -LiteralPath $nugetConfig -Encoding utf8
    $consumer = Join-Path $root 'tests/QuestionAppBuilder.PackageHost/QuestionAppBuilder.PackageHost.csproj'
    & dotnet restore $consumer --configfile $nugetConfig --packages $nuget --artifacts-path $artifacts "-p:PackageVersion=$Version" --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Package-only consumer restore failed.' }
    & dotnet build $consumer --configuration Release --no-restore --artifacts-path $artifacts "-p:PackageVersion=$Version" "-p:RestorePackagesPath=$nuget" --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Package-only consumer build failed.' }

    $portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $portProbe.Start(); $port = ([Net.IPEndPoint]$portProbe.LocalEndpoint).Port; $portProbe.Stop()
    $baseUrl = "http://127.0.0.1:$port"
    $dll = Join-Path $artifacts 'bin/QuestionAppBuilder.PackageHost/release/QuestionAppBuilder.PackageHost.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw "Consumer host output not found: $dll" }
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $hostProcess = Start-Process -FilePath 'dotnet' -ArgumentList @($dll,'--urls',$baseUrl) -WorkingDirectory $root -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($hostProcess.HasExited) { throw "Package host exited. $([IO.File]::ReadAllText($stderr))" }
        try { $null = Invoke-WebRequest -Uri "$baseUrl/Builder/PackageProof/Editor" -TimeoutSec 2; $ready = $true; break } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw "Package host did not become ready. $([IO.File]::ReadAllText($stderr))" }
    $editor = Invoke-WebRequest -Uri "$baseUrl/Builder/PackageProof/Editor"
    if ($editor.StatusCode -ne 200 -or $editor.Content -notmatch 'Applications' -or $editor.Content -notmatch 'Package consumer host') { throw 'Installed Editor package view or host layout did not render.' }
    $reports = Invoke-WebRequest -Uri "$baseUrl/Insights/PackageProof/Reports"
    if ($reports.StatusCode -ne 200 -or $reports.Content -notmatch 'Submission analysis') { throw 'Installed Reports package view did not render under its custom prefix.' }
    foreach ($asset in @('/_content/QuestionAppBuilder.Core/js/runtime.js','/_content/QuestionAppBuilder.Editor/js/editor.js','/_content/QuestionAppBuilder.Reports/css/reports.css')) {
        $response = Invoke-WebRequest -Uri "$baseUrl$asset"
        if ($response.StatusCode -ne 200 -or [string]::IsNullOrWhiteSpace($response.Content)) { throw "Installed package asset failed: $asset" }
    }
    $browserTests = Join-Path $root 'tests/QuestionAppBuilder.Browser.Tests/QuestionAppBuilder.Browser.Tests.csproj'
    & dotnet restore $browserTests --configfile $nugetConfig --packages $nuget --artifacts-path $artifacts "-p:PackageVersion=$Version" --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Package consumer browser-test restore failed.' }
    $env:QAB_PACKAGE_HOST_URL = $baseUrl
    & dotnet test $browserTests --no-restore --artifacts-path $artifacts --filter 'FullyQualifiedName~Installed_packages_render_assets_and_views_under_custom_prefix' --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Installed package consumer browser journey failed.' }
    Write-Output "Package consumption passed: six packages $Version, PackageReference-only .NET 10 host, Editor/Reports compiled views, and Core/Editor/Reports static assets."
} finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    if ($originalEnvironment) { $env:ASPNETCORE_ENVIRONMENT = $originalEnvironment } else { Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue }
    if ($null -eq $originalPackageHostUrl) { Remove-Item Env:QAB_PACKAGE_HOST_URL -ErrorAction SilentlyContinue }
    else { $env:QAB_PACKAGE_HOST_URL = $originalPackageHostUrl }
    if ($null -eq $originalRepositoryRoot) { Remove-Item Env:QAB_REPOSITORY_ROOT -ErrorAction SilentlyContinue }
    else { $env:QAB_REPOSITORY_ROOT = $originalRepositoryRoot }
    if ((Test-Path -LiteralPath $scratch) -and [IO.Path]::GetFullPath($scratch).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
