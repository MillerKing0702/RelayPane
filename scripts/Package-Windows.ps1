[CmdletBinding()]
param([string]$OutputPath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$OutputPath) { $OutputPath = Join-Path $root 'bin/RelayPane-1.0-Windows.zip' }
$destination = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath))
if (Test-Path -LiteralPath $destination) { throw 'Choose a ZIP output path that does not already exist.' }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null

$built = Join-Path $root 'bin/RelayPane.exe'
& (Join-Path $root 'Build.ps1') -OutputPath $built | Out-Null
$binary = Get-Item -LiteralPath $built
if ($binary.VersionInfo.ProductName -ne 'RelayPane' -or $binary.VersionInfo.ProductVersion -ne '1.0') {
    throw 'Expected the RelayPane 1.0 executable.'
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$stage = [IO.Path]::GetFullPath((Join-Path $tempRoot ('relaypane-package-' + [Guid]::NewGuid().ToString('N'))))
if ([IO.Path]::GetDirectoryName($stage) -ne $tempRoot) { throw 'Invalid package staging path.' }
$wrapper = Join-Path $stage 'RelayPane-1.0'
$zip = Join-Path $stage 'package.zip'
[IO.Directory]::CreateDirectory($wrapper) | Out-Null
try {
    [IO.File]::Copy($binary.FullName, (Join-Path $wrapper 'RelayPane.exe'))
    [IO.File]::Copy((Join-Path $root 'LICENSE'), (Join-Path $wrapper 'LICENSE'))
    [IO.File]::Copy((Join-Path $root 'docs/MarkdownNotices.txt'), (Join-Path $wrapper 'MarkdownNotices.txt'))
    [IO.Compression.ZipFile]::CreateFromDirectory($wrapper, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $expected = @('RelayPane-1.0/RelayPane.exe','RelayPane-1.0/LICENSE','RelayPane-1.0/MarkdownNotices.txt') | Sort-Object
        $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\','/') } | Sort-Object)
        if (Compare-Object $expected $entries) { throw 'Unexpected package contents.' }
    } finally { $archive.Dispose() }
    [IO.File]::Copy($zip, $destination)
} finally {
    if ([IO.Path]::GetDirectoryName($stage) -eq $tempRoot -and (Test-Path -LiteralPath $stage)) {
        Remove-Item -LiteralPath $stage -Recurse -ErrorAction SilentlyContinue
    }
}
Get-Item -LiteralPath $destination