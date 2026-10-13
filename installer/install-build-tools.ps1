[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot 'cache\InnoSetup')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dependencies.json') -Raw | ConvertFrom-Json
$Destination = [IO.Path]::GetFullPath($Destination)
$compiler = Join-Path $Destination 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) {
    Write-Output "Existing Inno Setup compiler: $compiler"
    return
}

New-Item -ItemType Directory -Path (Split-Path $Destination -Parent) -Force | Out-Null
$package = Join-Path (Split-Path $Destination -Parent) ("innosetup-" + $config.innoSetup.version + '.exe')
if (-not (Test-Path -LiteralPath $package)) {
    Invoke-WebRequest -Uri $config.innoSetup.url -OutFile $package
}
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $config.innoSetup.sha256) {
    throw 'The Inno Setup compiler download does not match its pinned SHA-256.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $package
if ($signature.Status -ne 'Valid') {
    throw "The Inno Setup compiler signature is not valid: $($signature.StatusMessage)"
}

Write-Output "Installing the verified compiler for the current user in $Destination"
$arguments = '/CURRENTUSER /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /DIR="' + $Destination + '"'
$process = Start-Process -FilePath $package -ArgumentList $arguments -PassThru -Wait
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler)) {
    throw "Compiler installation failed (exit $($process.ExitCode)). Install Inno Setup $($config.innoSetup.version) manually."
}
Write-Output "Compiler ready: $compiler"
