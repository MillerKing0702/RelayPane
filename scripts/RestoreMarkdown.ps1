param([Parameter(Mandatory = $true)][string]$Cache)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Directory]::CreateDirectory($Cache) | Out-Null

$packages = @(
    @('Markdig', '1.6.0', '5A41668881B286AA531AD37B5C9D4C88BF68DA5DF1CEC9F128F4FCE08E510BB2'),
    @('System.Memory', '4.6.3', '26078AEB758C9AE985E8BF851F973026061DA6A5EB4837204D0C2D2204C72955'),
    @('System.Buffers', '4.6.1', 'B00451E91D016FBEC091AD1E361F3A7015E1D91D4047F7E48A74455B2A673D79'),
    @('System.Numerics.Vectors', '4.6.1', '2BC500A86DCB02F2032D6D877F9E2D6E9E4A79080E57239B4198679D4031F2C7'),
    @('System.Runtime.CompilerServices.Unsafe', '6.1.2', '5F6A7F53AF3465F92BEB6DA873EBE0E496206C313313B98BADEE4355A6B25937')
)

foreach ($package in $packages) {
    $name, $version, $sha256 = $package
    $id = $name.ToLowerInvariant()
    $archive = Join-Path $Cache "$id.$version.nupkg"
    if (!(Test-Path -LiteralPath $archive)) {
        $download = "$archive.download"
        Invoke-WebRequest -UseBasicParsing -TimeoutSec 45 -Uri "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg" -OutFile $download
        if ((Get-FileHash -LiteralPath $download).Hash -ne $sha256) { throw "Unexpected archive checksum for $name" }
        Move-Item -LiteralPath $download -Destination $archive
    }
    if ((Get-FileHash -LiteralPath $archive).Hash -ne $sha256) { throw "Unexpected archived checksum for $name" }

    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $dll = Join-Path $Cache "$name.dll"
        $entry = $zip.GetEntry("lib/net462/$name.dll")
        if ($null -eq $entry) { throw "Missing .NET Framework assembly for $name" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dll, $true)
        $dll
    } finally { $zip.Dispose() }
}
