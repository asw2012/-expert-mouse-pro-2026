[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [string]$CompilerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$root = Split-Path $PSScriptRoot -Parent
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json') -Raw | ConvertFrom-Json
if (-not $Version) { $Version = $config.releaseVersion }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Release version must use three numeric components.' }
if (-not $CompilerPath) {
    $candidates = @(
        (Join-Path $PSScriptRoot 'cache\InnoSetup\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath)) {
    throw 'Inno Setup compiler is missing. Run .\installer\install-build-tools.ps1 first.'
}

$dist = Join-Path $root 'dist'
$publish = Join-Path $dist ("publish-" + $Version)
$packages = Join-Path $PSScriptRoot 'cache\nuget'
$project = Join-Path $root 'ExpertMouse\ExpertMouse.csproj'
New-Item -ItemType Directory -Path $publish -Force | Out-Null
$properties = @(
    "-p:RuntimeFrameworkVersion=$($config.dotnetRuntimeVersion)",
    "-p:Version=$Version",
    '-p:SelfContained=true'
)
& dotnet restore $project --runtime win-x64 --packages $packages @properties
if ($LASTEXITCODE -ne 0) { throw 'Self-contained runtime restore failed.' }
& dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --no-restore `
    --output $publish @properties
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }

$app = Join-Path $publish 'ExpertMouse.exe'
& $app --self-test
if ($LASTEXITCODE -ne 0) { throw 'The published app self-tests failed.' }
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination (Join-Path $publish 'APP-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $publish 'APP-NOTICES.txt')
foreach ($packageName in @('microsoft.netcore.app.runtime.win-x64', 'microsoft.windowsdesktop.app.runtime.win-x64')) {
    $packageDirectory = Join-Path $packages ($packageName + '\' + $config.dotnetRuntimeVersion)
    $notices = @(Get-ChildItem -LiteralPath $packageDirectory -File |
        Where-Object { $_.Name -match '(?i)^(license(?:\.txt)?|third.?party.?notices(?:\.txt)?)$' })
    if (-not ($notices | Where-Object { $_.Name -match '(?i)^license' })) {
        throw "Runtime package is missing its redistribution license: $packageName"
    }
    $licenseDirectory = Join-Path $publish ('runtime-licenses\' + $packageName)
    New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
    foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $licenseDirectory }
}

& $CompilerPath "/DAppVersion=$Version" "/DSourceRoot=$root" "/DPublishDir=$publish" `
    "/DOutputDir=$dist" "/DUsbPcapUrl=$($config.usbPcap.url)" `
    "/DUsbPcapSha256=$($config.usbPcap.sha256)" (Join-Path $PSScriptRoot 'ExpertMousePro2026.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

$installer = Join-Path $dist ("ExpertMousePro2026-Setup-" + $Version + '-win-x64.exe')
if (-not (Test-Path -LiteralPath $installer)) { throw 'Compiler did not produce the expected setup EXE.' }

$sourceZip = Join-Path $dist ("ExpertMousePro2026-Source-" + $Version + '.zip')
& (Join-Path $PSScriptRoot 'export-source.ps1') -OutputPath $sourceZip
if ($LASTEXITCODE -ne 0) { throw 'Source archive creation failed.' }
$hashes = foreach ($file in @($installer, $sourceZip)) {
    (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($file)
}
$hashes | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
& (Join-Path $PSScriptRoot 'test-package.ps1') -Version $Version
Write-Output "Installer ready: $installer"
Write-Output 'No repository, code, or release was uploaded.'
