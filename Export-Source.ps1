[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Destination)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$target = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination))
if ($root.TrimEnd('\') -eq $target.TrimEnd('\')) { throw 'Choose a separate export directory.' }

# Avoid copying from or through junctions or symlinks.
for ($dir = [IO.DirectoryInfo]$target; $null -ne $dir; $dir = $dir.Parent) {
    if ($dir.Exists -and ($dir.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The export path must not traverse a junction or symbolic link.'
    }
}
if (Test-Path -LiteralPath $target) {
    $existing = Get-Item -LiteralPath $target
    if (!$existing.PSIsContainer -or (Get-ChildItem -LiteralPath $target -Force | Select-Object -First 1)) {
        throw 'Choose a new or empty directory.'
    }
}

$files = @(Get-Content -LiteralPath (Join-Path $root 'ReleaseFiles.txt') | Where-Object { $_.Trim() })
$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($name in $files) {
    if ($name -notmatch '^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$' -or -not $seen.Add($name)) { throw 'Invalid release entry.' }
    $source = $root
    foreach ($segment in $name.Split('/')) {
        if ($segment -in @('.', '..') -or $segment.EndsWith('.') -or $segment -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw 'Invalid release entry segment.'
        }
        $source = Join-Path $source $segment
        if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Release entry must not traverse a symbolic link: $name"
        }
    }
    if ((Get-Item -LiteralPath $source).PSIsContainer) { throw "Release entry is a directory: $name" }
}

[IO.Directory]::CreateDirectory($target) | Out-Null
foreach ($name in $files) {
    $source = Join-Path $root $name
    $copy = Join-Path $target $name
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy)) | Out-Null
    [IO.File]::Copy($source, $copy)
    if ((Get-FileHash -LiteralPath $copy).Hash -ne (Get-FileHash -LiteralPath $source).Hash) {
        throw "Verification failed: $name"
    }
}
Get-Item -LiteralPath $target