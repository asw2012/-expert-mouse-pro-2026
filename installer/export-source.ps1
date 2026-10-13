[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$files = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($name in @('.gitignore', '.gitattributes', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')) {
    $files.Add((Get-Item -LiteralPath (Join-Path $root $name)))
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'ExpertMouse') -File |
    Where-Object { $_.Extension -in @('.cs', '.csproj') }) {
    $files.Add($file)
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'ExpertMouse\Assets') -File |
    Where-Object { $_.Extension -eq '.ico' }) {
    $files.Add($file)
}
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -File |
    Where-Object { $_.Extension -in @('.ps1', '.iss', '.json', '.txt') }) {
    $files.Add($file)
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root '.github\workflows') -File |
    Where-Object { $_.Extension -in @('.yml', '.yaml') }) {
    $files.Add($file)
}

Add-Type -AssemblyName System.IO.Compression
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path $OutputPath -Parent) -Force | Out-Null
$stream = [IO.File]::Open($OutputPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in $files | Sort-Object FullName) {
            $relative = [IO.Path]::GetRelativePath($root, $file.FullName)
            if ($relative.StartsWith('..') -or $relative -match '(?i)(^MouseWorks|(?:^|[\\/])captures(?:[\\/]|$)|(?:^|[\\/])settings\.json$|\.(?:pcap|pcapng|pfx|p12|exe)$)') {
                throw "File is not eligible for the public source archive: $relative"
            }
            $entry = $archive.CreateEntry($relative.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
            $entryStream = $entry.Open()
            try {
                $input = [IO.File]::OpenRead($file.FullName)
                try { $input.CopyTo($entryStream) }
                finally { $input.Dispose() }
            }
            finally { $entryStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}
finally { $stream.Dispose() }
Write-Output "Exported $($files.Count) original-source files; vendor packages, captures, settings and build outputs are excluded."
