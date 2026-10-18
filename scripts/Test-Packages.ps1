[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $FeedPath,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $SqlConnection,
    [switch] $KeepEvidence,
    [switch] $UseExistingFeed,
    [string] $PackageCachePath
)
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Package consumer acceptance requires Windows and SQL Server.' }
if (-not [IO.Path]::IsPathFullyQualified($FeedPath)) { throw '-FeedPath must be an absolute path.' }
if ($Version -notmatch '^\d+\.\d+\.\d+-[A-Za-z0-9][A-Za-z0-9.-]*$') { throw '-Version must be a unique prerelease SemVer.' }
if ([string]::IsNullOrWhiteSpace($SqlConnection)) { throw '-SqlConnection is required for a disposable package-consumer catalog.' }
$feed = [IO.Path]::GetFullPath($FeedPath)
if (Test-Path -LiteralPath $feed) {
    if (-not $UseExistingFeed -and (Get-ChildItem -LiteralPath $feed -Force | Measure-Object).Count -ne 0) { throw "Feed must be unique and empty: $feed" }
} elseif ($UseExistingFeed) { throw "-UseExistingFeed requires an existing populated feed: $feed" }
else { New-Item -ItemType Directory -Path $feed | Out-Null }
if ($PackageCachePath -and -not (Test-Path -LiteralPath ([IO.Path]::GetFullPath($PackageCachePath)) -PathType Container)) { throw "-PackageCachePath must name an existing package cache: $PackageCachePath" }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("qab-package-" + [guid]::NewGuid().ToString('N'))
$artifacts = Join-Path $scratch 'artifacts'
$results = Join-Path $scratch 'results'
$nuget = if ($PackageCachePath) { [IO.Path]::GetFullPath($PackageCachePath) } else { Join-Path $scratch 'packages' }
$nugetConfig = Join-Path $scratch 'NuGet.Config'
$certificatePath = Join-Path $scratch 'localhost.pfx'
$stdout = Join-Path $scratch 'host.stdout.log'
$stderr = Join-Path $scratch 'host.stderr.log'
$databaseName = 'QAB_Package_' + [guid]::NewGuid().ToString('N')
$hostProcess = $null
$databaseCreated = $false
$originalEnvironment = $env:ASPNETCORE_ENVIRONMENT
$originalUrls = $env:ASPNETCORE_URLS
$originalCertPath = $env:ASPNETCORE_Kestrel__Certificates__Default__Path
$originalCertPassword = $env:ASPNETCORE_Kestrel__Certificates__Default__Password
$originalPackageHostUrl = $env:QAB_PACKAGE_HOST_URL
$originalPackageSql = $env:QAB_PACKAGE_SQL
$originalPackageBaseUrl = $env:QAB_PACKAGE_BASE_URL
$originalRepositoryRoot = $env:QAB_REPOSITORY_ROOT
$certificatePassword = 'QabPackageAcceptance-Temporary!'
function Get-ReadableHostLog([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '' }
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try { $reader = [IO.StreamReader]::new($stream); try { return $reader.ReadToEnd() } finally { $reader.Dispose() } }
        finally { $stream.Dispose() }
    } catch { return "(log temporarily unavailable: $($_.Exception.Message))" }
}
try {
    New-Item -ItemType Directory -Path $scratch, $results | Out-Null
    $env:QAB_REPOSITORY_ROOT = $root
    $projects = @(
        'src/QuestionAppBuilder.Domain/QuestionAppBuilder.Domain.csproj',
        'src/QuestionAppBuilder.Application/QuestionAppBuilder.Application.csproj',
        'src/QuestionAppBuilder.Infrastructure/QuestionAppBuilder.Infrastructure.csproj',
        'src/QuestionAppBuilder.Core/QuestionAppBuilder.Core.csproj',
        'src/QuestionAppBuilder.Editor/QuestionAppBuilder.Editor.csproj',
        'src/QuestionAppBuilder.Reports/QuestionAppBuilder.Reports.csproj'
    )
    if (-not $UseExistingFeed) {
        foreach ($project in $projects) {
            & dotnet pack (Join-Path $root $project) --configuration Release --artifacts-path $artifacts --output $feed "-p:PackageVersion=$Version" --verbosity minimal
            if ($LASTEXITCODE -ne 0) { throw "Packing failed for $project (exit $LASTEXITCODE). Evidence: $scratch" }
        }
    }
    $expected = @('QuestionAppBuilder.Domain','QuestionAppBuilder.Application','QuestionAppBuilder.Infrastructure','QuestionAppBuilder.Core','QuestionAppBuilder.Editor','QuestionAppBuilder.Reports')
    $actual = @(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' | ForEach-Object { $_.BaseName -replace '\.\d+\.\d+\.\d+-.*$','' })
    foreach ($id in $expected) {
        if ($actual -notcontains $id) { throw "Package '$id' was not produced. Evidence: $scratch" }
        if (-not (Test-Path -LiteralPath (Join-Path $feed "$id.$Version.nupkg"))) { throw "Package '$id' version '$Version' is not available in the feed. Evidence: $scratch" }
    }

    $feedXml = [Security.SecurityElement]::Escape($feed)
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="private-feed" value="$feedXml"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
"@ | Set-Content -LiteralPath $nugetConfig -Encoding utf8
    $consumer = Join-Path $root 'tests/QuestionAppBuilder.PackageHost/QuestionAppBuilder.PackageHost.csproj'
    & dotnet restore $consumer --configfile $nugetConfig --packages $nuget --artifacts-path $artifacts "-p:PackageVersion=$Version" --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Package-only consumer restore failed. Evidence: $scratch" }
    & dotnet build $consumer --configuration Release --no-restore --artifacts-path $artifacts "-p:PackageVersion=$Version" "-p:RestorePackagesPath=$nuget" --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Package-only consumer build failed. Evidence: $scratch" }
    $dll = Join-Path $artifacts 'bin/QuestionAppBuilder.PackageHost/release/QuestionAppBuilder.PackageHost.dll'
    if (-not (Test-Path -LiteralPath $dll)) { throw "Consumer host output not found: $dll" }

    $sqlcmd = Get-Command sqlcmd -ErrorAction SilentlyContinue
    if (-not $sqlcmd) { throw 'sqlcmd is required to create and remove the owned disposable SQL catalog.' }
    $sqlBuilder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $sqlBuilder.set_ConnectionString($SqlConnection)
    $serverKey = @($sqlBuilder.Keys | Where-Object { $_ -match '^(server|data source|address|addr|network address)$' } | Select-Object -First 1)
    if ($serverKey.Count -eq 0) { throw 'The SQL connection string must include a server/data source.' }
    $server = [string]$sqlBuilder[$serverKey[0]]
    $sqlArgs = @('-S', $server, '-b')
    $integratedKey = @($sqlBuilder.Keys | Where-Object { $_ -match '^(integrated security|trusted_connection)$' } | Select-Object -First 1)
    if ($integratedKey.Count -gt 0 -and [string]$sqlBuilder[$integratedKey[0]] -match '^(true|yes|sspi)$') { $sqlArgs += '-E' }
    else {
        $userKey = @($sqlBuilder.Keys | Where-Object { $_ -match '^(user id|uid)$' } | Select-Object -First 1)
        $passwordKey = @($sqlBuilder.Keys | Where-Object { $_ -match '^(password|pwd)$' } | Select-Object -First 1)
        if ($userKey.Count -eq 0 -or $passwordKey.Count -eq 0) { throw 'Specify Windows integrated security or SQL user/password in -SqlConnection.' }
        $sqlArgs += @('-U', [string]$sqlBuilder[$userKey[0]], '-P', [string]$sqlBuilder[$passwordKey[0]])
    }
    & $sqlcmd.Source @sqlArgs '-Q' "CREATE DATABASE [$databaseName]" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not create disposable SQL catalog '$databaseName' (sqlcmd exit $LASTEXITCODE)." }
    $databaseCreated = $true
    foreach ($catalogKey in @($sqlBuilder.Keys | Where-Object { $_ -match '^(initial catalog|database)$' })) { $sqlBuilder.Remove($catalogKey) }
    $sqlBuilder['Initial Catalog'] = $databaseName
    $sqlBuilder['Connect Timeout'] = 5
    $env:QAB_PACKAGE_SQL = $sqlBuilder.get_ConnectionString()

    & dotnet dev-certs https --export-path $certificatePath --password $certificatePassword
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $certificatePath)) { throw "Could not create isolated HTTPS certificate. Evidence: $scratch" }
    $portProbe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $portProbe.Start(); $port = ([Net.IPEndPoint]$portProbe.LocalEndpoint).Port; $portProbe.Stop()
    $baseUrl = "https://localhost:$port"
    $env:QAB_PACKAGE_BASE_URL = $baseUrl
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $baseUrl
    $env:ASPNETCORE_Kestrel__Certificates__Default__Path = $certificatePath
    $env:ASPNETCORE_Kestrel__Certificates__Default__Password = $certificatePassword
    $hostProcess = Start-Process -FilePath 'dotnet' -ArgumentList @($dll,'--urls',$baseUrl) -WorkingDirectory $root -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $ready = $false
    $lastReadinessError = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($hostProcess.HasExited) { throw "Package host exited. $(Get-ReadableHostLog $stderr) Evidence: $scratch" }
        try {
            $response = Invoke-WebRequest -Uri "$baseUrl/Builder/Applications" -SkipCertificateCheck -SkipHttpErrorCheck -TimeoutSec 3
            if ([int]$response.StatusCode -in @(200,401,403)) { $ready = $true; break }
            $lastReadinessError = "Unexpected readiness HTTP status $($response.StatusCode)."
        } catch {
            $lastReadinessError = "$( $_.Exception.GetType().Name ): $( $_.Exception.Message -replace '(?i)(password|pwd)\s*=\s*[^;\s]+', '$1=<redacted>' )"
            Start-Sleep -Milliseconds 250
        }
    }
    if (-not $ready) { throw "Package host did not become ready. Last readiness error: $lastReadinessError. $(Get-ReadableHostLog $stderr) $(Get-ReadableHostLog $stdout) Evidence: $scratch" }
    foreach ($asset in @('/_content/QuestionAppBuilder.Core/js/runtime.js','/_content/QuestionAppBuilder.Editor/js/editor.js','/_content/QuestionAppBuilder.Reports/css/reports.css')) {
        $response = Invoke-WebRequest -Uri "$baseUrl$asset" -SkipCertificateCheck -SkipHttpErrorCheck -TimeoutSec 10
        if ($response.StatusCode -ne 200 -or [string]::IsNullOrWhiteSpace($response.Content)) { throw "Installed package asset failed: $asset. Evidence: $scratch" }
    }

    $browserTests = Join-Path $root 'tests/QuestionAppBuilder.Browser.Tests/QuestionAppBuilder.Browser.Tests.csproj'
    & dotnet restore $browserTests --configfile $nugetConfig --packages $nuget --artifacts-path $artifacts "-p:PackageVersion=$Version" --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Package consumer browser-test restore failed. Evidence: $scratch" }
    $env:QAB_PACKAGE_HOST_URL = $baseUrl
    & dotnet test $browserTests --no-restore --artifacts-path $artifacts --results-directory $results --logger 'trx;LogFileName=PackageConsumer.trx' --filter 'FullyQualifiedName~Installed_packages_render_assets_and_views_under_custom_prefix' --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw "Installed package consumer browser journey failed (exit $LASTEXITCODE). Host logs: $(Get-ReadableHostLog $stderr) Evidence: $scratch" }
    Write-Output "Package consumption passed: six packages $Version, PackageReference-only .NET 10 host, custom-prefix controllers/views/assets, real LocalDB author→publish→email grant→response→submit→report/export journey, and editor/runtime axe scans. Evidence: $scratch"
} finally {
    if ($hostProcess -and -not $hostProcess.HasExited) {
        try {
            $null = Invoke-WebRequest -Uri ($env:QAB_PACKAGE_HOST_URL + '/PackageTest/Shutdown') -Method Post -SkipCertificateCheck -TimeoutSec 5
            $null = $hostProcess.WaitForExit(10000)
        } catch { }
        if (-not $hostProcess.HasExited) { Stop-Process -Id $hostProcess.Id -Force -ErrorAction SilentlyContinue }
    }
    if ($databaseCreated) {
        try {
            & $sqlcmd.Source @sqlArgs '-Q' "ALTER DATABASE [$databaseName] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$databaseName]" | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Warning "sqlcmd could not remove owned package acceptance database '$databaseName' (exit $LASTEXITCODE)." }
        } catch { Write-Warning "Could not remove owned package acceptance database '$databaseName': $_" }
    }
    if ($null -eq $originalEnvironment) { Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue } else { $env:ASPNETCORE_ENVIRONMENT = $originalEnvironment }
    if ($null -eq $originalUrls) { Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue } else { $env:ASPNETCORE_URLS = $originalUrls }
    if ($null -eq $originalCertPath) { Remove-Item Env:ASPNETCORE_Kestrel__Certificates__Default__Path -ErrorAction SilentlyContinue } else { $env:ASPNETCORE_Kestrel__Certificates__Default__Path = $originalCertPath }
    if ($null -eq $originalCertPassword) { Remove-Item Env:ASPNETCORE_Kestrel__Certificates__Default__Password -ErrorAction SilentlyContinue } else { $env:ASPNETCORE_Kestrel__Certificates__Default__Password = $originalCertPassword }
    if ($null -eq $originalPackageHostUrl) { Remove-Item Env:QAB_PACKAGE_HOST_URL -ErrorAction SilentlyContinue } else { $env:QAB_PACKAGE_HOST_URL = $originalPackageHostUrl }
    if ($null -eq $originalPackageSql) { Remove-Item Env:QAB_PACKAGE_SQL -ErrorAction SilentlyContinue } else { $env:QAB_PACKAGE_SQL = $originalPackageSql }
    if ($null -eq $originalPackageBaseUrl) { Remove-Item Env:QAB_PACKAGE_BASE_URL -ErrorAction SilentlyContinue } else { $env:QAB_PACKAGE_BASE_URL = $originalPackageBaseUrl }
    if ($null -eq $originalRepositoryRoot) { Remove-Item Env:QAB_REPOSITORY_ROOT -ErrorAction SilentlyContinue } else { $env:QAB_REPOSITORY_ROOT = $originalRepositoryRoot }
    if ((Test-Path -LiteralPath $scratch) -and -not $KeepEvidence -and [IO.Path]::GetFullPath($scratch).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
