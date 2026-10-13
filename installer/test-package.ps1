[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$publish = Join-Path $dist ("publish-" + $Version)
foreach ($name in @('ExpertMouse.exe', 'System.Private.CoreLib.dll', 'System.Drawing.Common.dll',
    'System.Windows.Forms.dll', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'APP-LICENSE.txt', 'APP-NOTICES.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $name))) {
        throw "Missing self-contained distribution file: $name"
    }
}
$runtime = Get-Content -LiteralPath (Join-Path $publish 'ExpertMouse.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.PSObject.Properties.Name -contains 'framework') {
    throw 'The release is not a self-contained runtime distribution.'
}
$metadata = (Get-Item -LiteralPath (Join-Path $publish 'ExpertMouse.exe')).VersionInfo
if ($metadata.ProductName -ne 'Expert Mouse Pro 2026') { throw 'Published product name is incorrect.' }
$installerSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'ExpertMousePro2026.iss') -Raw
if ($installerSource -match "(?is)USBPcapSetup-1\.5\.4\.0\.exe'\)\s*,\s*'/S'") {
    throw 'USBPcap license prompts must not be bypassed with silent setup.'
}
if ($installerSource -notmatch "(?is)USBPcapSetup-1\.5\.4\.0\.exe'\)\s*,\s*''\s*,\s*''\s*,\s*SW_SHOWNORMAL") {
    throw 'USBPcap original installer must be launched visibly and interactively.'
}
if (-not (Test-Path -LiteralPath (Join-Path $dist ("ExpertMousePro2026-Setup-" + $Version + '-win-x64.exe')))) {
    throw 'Setup EXE is missing.'
}
$archivePath = Join-Path $dist ("ExpertMousePro2026-Source-" + $Version + '.zip')
Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entries = @($archive.Entries | Select-Object -ExpandProperty FullName)
    foreach ($required in @('LICENSE', 'README.md', 'ExpertMouse/Program.cs',
        'ExpertMouse/Assets/App.ico', 'installer/ExpertMousePro2026.iss', '.github/workflows/release.yml')) {
        if ($entries -notcontains $required) { throw "Source archive is missing $required" }
    }
    if ($entries | Where-Object { $_ -match '(?i)(MouseWorks|captures/|/bin/|/obj/|/app/|cache/|settings\.json|\.pcap|\.pfx|\.p12|\.exe$)' }) {
        throw 'Public source archive contains prohibited vendor, personal, or binary files.'
    }
}
finally { $archive.Dispose() }
Write-Output 'PASS: Setup asset, self-contained .NET runtime, original-source archive, licenses and branding.'
