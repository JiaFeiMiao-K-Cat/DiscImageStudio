#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$Tag,
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'The Windows executable and its WPF smoke checks must be built on Windows.'
}

$expectedArchitecture = if ($RuntimeIdentifier -eq 'win-arm64') { 'Arm64' } else { 'X64' }
$osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
if ($osArchitecture -ne $expectedArchitecture -or $processArchitecture -ne $expectedArchitecture) {
    throw "Release $RuntimeIdentifier requires native $expectedArchitecture Windows and PowerShell for its smoke checks; OS=$osArchitecture, process=$processArchitecture."
}

$metadata = & (Join-Path $PSScriptRoot 'Get-ReleaseMetadata.ps1') -Tag $Tag
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspacePrefix = $workspaceRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$releaseDirectory = [IO.Path]::GetFullPath((Join-Path $workspaceRoot "artifacts\release\$($metadata.Tag)\$RuntimeIdentifier"))

function Assert-SafeReleasePath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($workspacePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Release output resolved outside the workspace: $fullPath"
    }

    # A junction or symbolic link could make a lexically safe path leave the workspace.
    $currentPath = $fullPath
    while ($currentPath.Length -ge $workspaceRoot.Length) {
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item -LiteralPath $currentPath -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Release output must not pass through a junction or symbolic link: $currentPath"
            }
        }
        if ($currentPath.Equals($workspaceRoot, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $currentPath = [IO.Path]::GetDirectoryName($currentPath)
    }
}

Assert-SafeReleasePath $releaseDirectory
if (Test-Path -LiteralPath $releaseDirectory) {
    $linkedItems = @(Get-ChildItem -LiteralPath $releaseDirectory -Force -Recurse |
        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    if ($linkedItems.Count -ne 0) {
        throw "Refusing to clear a release directory containing junctions or symbolic links: $releaseDirectory"
    }
    # Only this validated tag/RID output is replaced; other architectures are retained.
    Remove-Item -LiteralPath $releaseDirectory -Recurse -Force
}

$publishDirectory = Join-Path $releaseDirectory 'publish'
$smokeDirectory = Join-Path $releaseDirectory 'smoke'
$isolatedAppDirectory = Join-Path $smokeDirectory 'app'
$projectPath = Join-Path $workspaceRoot 'src\DiscImageStudio.App\DiscImageStudio.App.csproj'
New-Item -ItemType Directory -Path $publishDirectory, $isolatedAppDirectory -Force | Out-Null

# WPF's temporary project and the app can build the DVD executable with different
# global properties but the same runtimeconfig output. Serialize this publish graph.
& dotnet publish $projectPath `
    --configuration Release `
    --runtime $RuntimeIdentifier `
    --self-contained true `
    --output $publishDirectory `
    -maxcpucount:1 `
    -p:BuildInParallel=false `
    "-p:Version=$($metadata.Version)" `
    "-p:AssemblyVersion=$($metadata.FileVersion)" `
    "-p:FileVersion=$($metadata.FileVersion)" `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$publishedExecutable = Join-Path $publishDirectory 'DiscImageStudio.exe'
if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "Published executable is missing: $publishedExecutable"
}
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExecutable).FileVersion
if ($fileVersion -ne $metadata.FileVersion) {
    throw "Published executable version '$fileVersion' does not match '$($metadata.FileVersion)'."
}

Get-ChildItem -LiteralPath $publishDirectory -Filter '*.pdb' -File -Recurse -Force |
    Remove-Item -Force
$referencedRuntimeConfig = Join-Path $publishDirectory 'DvdImageSolver.runtimeconfig.json'
if (Test-Path -LiteralPath $referencedRuntimeConfig) {
    Remove-Item -LiteralPath $referencedRuntimeConfig -Force
}

$packageFileName = "DiscImageStudio-$($metadata.Tag)-$RuntimeIdentifier.exe"
$packagePath = Join-Path $releaseDirectory $packageFileName
Copy-Item -LiteralPath $publishedExecutable -Destination $packagePath
$peInfo = & (Join-Path $PSScriptRoot 'Assert-WindowsReleaseExecutable.ps1') `
    -Path $packagePath -RuntimeIdentifier $RuntimeIdentifier
Write-Host "Verified release PE: Machine=$($peInfo.Machine); CET-compatible=$($peInfo.CetCompatible)"
$checkExecutable = Join-Path $isolatedAppDirectory $packageFileName
Copy-Item -LiteralPath $packagePath -Destination $checkExecutable

function ConvertTo-WindowsArgument([string]$Value) {
    # Start-Process joins ArgumentList into a command line. Quote each argument using
    # Windows' backslash/quote rules so paths containing spaces arrive intact.
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Invoke-PublishedCheck([string]$Name, [string[]]$Arguments) {
    Write-Host "Checking published package: $Name"
    $commandLine = ($Arguments | ForEach-Object { ConvertTo-WindowsArgument $_ }) -join ' '
    $process = Start-Process -FilePath $checkExecutable `
        -ArgumentList $commandLine `
        -WorkingDirectory $isolatedAppDirectory `
        -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $smokeDirectory "$Name.stdout.log") `
        -RedirectStandardError (Join-Path $smokeDirectory "$Name.stderr.log") `
        -Wait -PassThru
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ($exitCode -ne 0) {
        throw "Published package check '$Name' failed with exit code $exitCode. See $smokeDirectory for logs."
    }
}

function Assert-FileLength([string]$Path, [long]$ExpectedLength) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Smoke check did not produce its expected output: $Path"
    }
    $actualLength = (Get-Item -LiteralPath $Path).Length
    if ($actualLength -ne $ExpectedLength) {
        throw "Smoke output '$Path' has $actualLength bytes; expected $ExpectedLength."
    }
}

Invoke-PublishedCheck 'burn-build-info' @('burn-build-info')
Invoke-PublishedCheck 'selftest' @('selftest')

# Spaces in these paths also exercise argument quoting in the packaged WinExe.
$inputImage = Join-Path $smokeDirectory 'input image.png'
Copy-Item -LiteralPath (Join-Path $workspaceRoot 'src\DiscImageStudio.App\Assets\DiscImageStudio-256.png') `
    -Destination $inputImage
$rawPath = Join-Path $smokeDirectory 'two sectors.raw'
Invoke-PublishedCheck 'cd-raw' @('cd-generate', '--input', $inputImage, '--output', $rawPath, '--sectors', '2')
Assert-FileLength $rawPath 4704

$wavePath = Join-Path $smokeDirectory 'two sectors.wav'
Invoke-PublishedCheck 'cd-wav' @('cd-generate', '--input', $inputImage, '--output', $wavePath, '--sectors', '2')
Assert-FileLength $wavePath 4748
$cuePath = [IO.Path]::ChangeExtension($wavePath, '.cue')
if (-not (Test-Path -LiteralPath $cuePath -PathType Leaf) -or (Get-Item -LiteralPath $cuePath).Length -eq 0) {
    throw "WAV generation did not produce a CUE sheet: $cuePath"
}

# The CUE sheet is opt-out: --cue false must still write the WAV and leave no .cue behind.
$noCuePath = Join-Path $smokeDirectory 'no cue.wav'
Invoke-PublishedCheck 'cd-wav-no-cue' @(
    'cd-generate', '--input', $inputImage, '--output', $noCuePath, '--sectors', '2', '--cue', 'false')
Assert-FileLength $noCuePath 4748
if (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($noCuePath, '.cue'))) {
    throw "cd-generate --cue false still produced a CUE sheet for $noCuePath"
}

$snapshotPath = Join-Path $smokeDirectory 'ui snapshot.png'
Invoke-PublishedCheck 'ui-snapshot' @('ui-snapshot', '--output', $snapshotPath)
if (-not (Test-Path -LiteralPath $snapshotPath -PathType Leaf) -or (Get-Item -LiteralPath $snapshotPath).Length -eq 0) {
    throw "WPF smoke check did not produce a UI snapshot: $snapshotPath"
}

$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ((Get-FileHash -LiteralPath $checkExecutable -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) {
    throw 'The isolated executable does not match the release executable.'
}
$checksumPath = Join-Path $releaseDirectory 'SHA256SUMS.txt'
$checksumLine = "$hash  $packageFileName`n"
[IO.File]::WriteAllText($checksumPath, $checksumLine, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText("$packagePath.sha256", $checksumLine, [Text.UTF8Encoding]::new($false))
Write-Host "Single executable release ready: $packagePath"
Write-Host "Checksums: $checksumPath"
