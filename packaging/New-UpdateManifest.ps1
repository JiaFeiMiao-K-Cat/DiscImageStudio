[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ReleaseDirectory,
    [Parameter(Mandatory)]
    [string]$Tag,
    [Parameter(Mandatory)]
    [string]$Repository,
    [string]$ServerUrl = 'https://github.com'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Tag -cnotmatch '^v(?<version>(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-(alpha|beta|rc)\.[1-9][0-9]*)?)$') {
    throw 'Tag must be vA.B.C or vA.B.C-(alpha|beta|rc).N, with N starting at 1.'
}
$version = $Matches['version']
if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
    throw 'Repository must be owner/name.'
}
if ($ServerUrl -cnotmatch '^https://[A-Za-z0-9.-]+(?::[0-9]+)?$') {
    throw 'ServerUrl must be an HTTPS origin without a trailing slash.'
}

$releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory)
if (-not (Test-Path -LiteralPath $releaseRoot -PathType Container)) {
    throw "Release directory does not exist: $releaseRoot"
}

$assets = [ordered]@{}
foreach ($rid in 'win-x64', 'win-arm64') {
    $fileName = "DiscImageStudio-$Tag-$rid.exe"
    $path = Join-Path $releaseRoot $fileName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Missing tested package: $fileName"
    }

    $length = (Get-Item -LiteralPath $path).Length
    if ($length -le 0) {
        throw "Tested package is empty: $fileName"
    }

    $assets[$rid] = [ordered]@{
        url = "$ServerUrl/$Repository/releases/download/$Tag/$fileName"
        sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        size = [long]$length
    }
}

$manifest = [ordered]@{
    version = $version
    tag = $Tag
    notesUrl = "$ServerUrl/$Repository/releases/tag/$Tag"
    assets = $assets
}
$json = ($manifest | ConvertTo-Json -Depth 5) -replace "`r`n", "`n"
$manifestPath = Join-Path $releaseRoot 'latest.json'
[IO.File]::WriteAllText($manifestPath, "$json`n", [Text.UTF8Encoding]::new($false))
Write-Host "Update manifest ready: $manifestPath"
