$ErrorActionPreference = "Stop"

$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$fixtureParent = [IO.Path]::GetFullPath((Join-Path $workspaceRoot "artifacts\tests\release-tooling"))
$fixtureRoot = [IO.Path]::GetFullPath((Join-Path $fixtureParent ([guid]::NewGuid().ToString("N"))))
$fixturePackaging = Join-Path $fixtureRoot "packaging"
$fixtureHelper = Join-Path $fixturePackaging "Get-ReleaseMetadata.ps1"
$fixtureManifestHelper = Join-Path $fixturePackaging "New-UpdateManifest.ps1"
$testCount = 0

function Assert-ChildPath([string]$Path, [string]$Parent) {
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd([char[]]"\/") + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Test fixture path resolved outside its expected directory: $resolvedPath"
    }
}

function Assert-NoReparsePoint([string]$Path) {
    $currentPath = [IO.Path]::GetFullPath($Path)
    while ($currentPath -ne $workspaceRoot) {
        Assert-ChildPath $currentPath $workspaceRoot
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item -LiteralPath $currentPath -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Test fixture path must not traverse a reparse point: $currentPath"
            }
        }
        $currentPath = [IO.Path]::GetDirectoryName($currentPath)
    }
}

function Set-Fixture {
    param(
        [string]$Version = "1.2.3",
        [string]$VersionSuffix,
        [string]$Changelog = "# Changelog`n`n## Unreleased`n`n- Pending work.`n"
    )

    $escapedVersion = [Security.SecurityElement]::Escape($Version)
    $suffixElement = ""
    if ($PSBoundParameters.ContainsKey("VersionSuffix")) {
        $escapedSuffix = [Security.SecurityElement]::Escape($VersionSuffix)
        $suffixElement = "<VersionSuffix>$escapedSuffix</VersionSuffix>"
    }
    $props = "<Project><PropertyGroup><VersionPrefix>$escapedVersion</VersionPrefix>$suffixElement</PropertyGroup></Project>"
    [IO.File]::WriteAllText((Join-Path $fixtureRoot "Directory.Build.props"), $props, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $fixtureRoot "CHANGELOG.md"), $Changelog, [Text.UTF8Encoding]::new($false))
}

function Set-UpdateFixture {
    param(
        [string]$Tag = "v1.2.3",
        [switch]$OmitArm64
    )

    $release = Join-Path $fixtureRoot "release"
    if (Test-Path -LiteralPath $release) {
        Remove-Item -LiteralPath $release -Recurse -Force
    }
    New-Item -ItemType Directory -Path $release -Force | Out-Null
    foreach ($rid in 'win-x64', 'win-arm64') {
        if ($OmitArm64 -and $rid -eq 'win-arm64') {
            continue
        }
        $content = [byte[]](1..(16 + $rid.Length))
        [IO.File]::WriteAllBytes((Join-Path $release "DiscImageStudio-$Tag-$rid.exe"), $content)
    }
    return $release
}

function Assert-Equal($Actual, $Expected, [string]$Description) {
    if ($Actual -cne $Expected) {
        throw "$Description`: expected '$Expected', received '$Actual'."
    }
}

function Assert-Rejected([scriptblock]$Action, [string]$ExpectedMessage) {
    $failure = $null
    try {
        $null = & $Action
    }
    catch {
        $failure = $_
    }
    if ($null -eq $failure) {
        throw "Expected release metadata validation to reject this fixture."
    }
    if ($failure.Exception.Message -notmatch $ExpectedMessage) {
        throw "Fixture failed for an unexpected reason: $($failure.Exception.Message)"
    }
}

function Invoke-Test([string]$Name, [scriptblock]$Action) {
    try {
        & $Action
        $script:testCount++
    }
    catch {
        throw "Release tooling test '$Name' failed: $($_.Exception.Message)"
    }
}

Assert-ChildPath $fixtureParent (Join-Path $workspaceRoot "artifacts")
Assert-ChildPath $fixtureRoot $fixtureParent
Assert-NoReparsePoint $fixtureRoot

try {
    New-Item -ItemType Directory -Path $fixturePackaging -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $workspaceRoot "packaging\Get-ReleaseMetadata.ps1") -Destination $fixtureHelper
    Copy-Item -LiteralPath (Join-Path $workspaceRoot "packaging\New-UpdateManifest.ps1") -Destination $fixtureManifestHelper

    Invoke-Test "default version metadata without VersionSuffix remains supported" {
        Set-Fixture
        $metadata = & $fixtureHelper
        Assert-Equal $metadata.Tag "v1.2.3" "default tag"
        Assert-Equal $metadata.Version "1.2.3" "default version"
        Assert-Equal $metadata.BaseVersion "1.2.3" "base version"
        Assert-Equal $metadata.FileVersion "1.2.3.0" "file version"
        Assert-Equal $metadata.Prerelease $false "stable status"
        Assert-Equal $metadata.Notes $null "optional release notes"
    }

    Invoke-Test "configured prerelease suffix supplies the default tag and notes" {
        Set-Fixture -VersionSuffix "rc.1" -Changelog "## [1.2.3-rc.1] - 2026-09-12`n- Release candidate.`n"
        $metadata = & $fixtureHelper -RequireReleaseNotes
        Assert-Equal $metadata.Tag "v1.2.3-rc.1" "configured default tag"
        Assert-Equal $metadata.Version "1.2.3-rc.1" "configured prerelease version"
        Assert-Equal $metadata.BaseVersion "1.2.3" "configured base version"
        Assert-Equal $metadata.FileVersion "1.2.3.0" "configured file version"
        Assert-Equal $metadata.Prerelease $true "configured prerelease status"
        Assert-Equal $metadata.Notes "- Release candidate." "configured release notes"
    }

    Invoke-Test "empty configured suffix remains stable" {
        Set-Fixture -VersionSuffix ""
        $metadata = & $fixtureHelper
        Assert-Equal $metadata.Tag "v1.2.3" "empty suffix tag"
        Assert-Equal $metadata.Prerelease $false "empty suffix stable status"
    }

    foreach ($suffix in @("preview.1", "rc.0", "RC.1")) {
        Invoke-Test "reject invalid configured suffix $suffix" {
            Set-Fixture -VersionSuffix $suffix
            Assert-Rejected { & $fixtureHelper } "VersionSuffix must be empty or"
        }
    }

    Invoke-Test "explicit stable tag" {
        Set-Fixture
        $metadata = & $fixtureHelper -Tag "v1.2.3"
        Assert-Equal $metadata.Tag "v1.2.3" "stable tag"
        Assert-Equal $metadata.Prerelease $false "stable status"
    }

    foreach ($channel in @("alpha", "beta", "rc")) {
        Invoke-Test "$channel prerelease metadata and notes" {
            $version = "1.2.3-$channel.2"
            Set-Fixture -Changelog "# Changelog`n`n## [$version] - 2026-09-12`n`n- Test $channel release.`n"
            $metadata = & $fixtureHelper -Tag "v$version" -RequireReleaseNotes
            Assert-Equal $metadata.Version $version "prerelease version"
            Assert-Equal $metadata.BaseVersion "1.2.3" "prerelease base version"
            Assert-Equal $metadata.FileVersion "1.2.3.0" "prerelease file version"
            Assert-Equal $metadata.Prerelease $true "prerelease status"
            Assert-Equal $metadata.Notes "- Test $channel release." "prerelease notes"
        }
    }

    foreach ($tag in @(
        "1.2.3", "V1.2.3", "v01.2.3", "v1.02.3", "v1.2.03",
        "v1.2.3-preview.1", "v1.2.3-RC.1", "v1.2.3-rc", "v1.2.3-rc.0",
        "v1.2.3-rc.01", "v1.2.3-rc.-1", "v1.2.3-rc.1.extra", "v1.2.3+build.1",
        "v1.2.3.0", " v1.2.3", "v1.2.3 "
    )) {
        Invoke-Test "reject invalid tag $tag" {
            Set-Fixture
            Assert-Rejected { & $fixtureHelper -Tag $tag } "Tag must be"
        }
    }

    Invoke-Test "reject mismatched tag version" {
        Set-Fixture
        Assert-Rejected { & $fixtureHelper -Tag "v1.2.4" } "does not match VersionPrefix"
    }

    Invoke-Test "maximum assembly version components" {
        Set-Fixture -Version "65534.65534.65534"
        $metadata = & $fixtureHelper
        Assert-Equal $metadata.FileVersion "65534.65534.65534.0" "maximum file version"
    }

    foreach ($version in @("65535.2.3", "1.65535.3", "1.2.65535", "65536.2.3")) {
        Invoke-Test "reject version outside assembly range $version" {
            Set-Fixture -Version $version
            Assert-Rejected { & $fixtureHelper } "Version components must be in 0\.\.65534"
        }
    }

    foreach ($version in @("01.2.3", "1.02.3", "1.2.03", "1.2", "1.2.3.0", "1.2.3-rc.1")) {
        Invoke-Test "reject malformed VersionPrefix $version" {
            Set-Fixture -Version $version
            Assert-Rejected { & $fixtureHelper } "must define one VersionPrefix in A.B.C format"
        }
    }

    Invoke-Test "reject missing release notes" {
        Set-Fixture
        Assert-Rejected { & $fixtureHelper -RequireReleaseNotes } "must contain exactly one"
    }

    Invoke-Test "reject empty release notes" {
        Set-Fixture -Changelog "# Changelog`n`n## [1.2.3] - 2026-09-12`n `n`t`n## [1.2.2] - 2026-09-01`n- Older release.`n"
        Assert-Rejected { & $fixtureHelper -RequireReleaseNotes } "must not be empty"
    }

    Invoke-Test "reject duplicate release notes" {
        Set-Fixture -Changelog "## [1.2.3] - 2026-09-12`n- First.`n`n## [1.2.3] - 2026-09-11`n- Second.`n"
        Assert-Rejected { & $fixtureHelper -RequireReleaseNotes } "must contain exactly one"
    }

    Invoke-Test "reject invalid calendar date" {
        Set-Fixture -Changelog "## [1.2.3] - 2026-02-30`n- Invalid date.`n"
        Assert-Rejected { & $fixtureHelper -RequireReleaseNotes } "ParseExact"
    }

    Invoke-Test "reject malformed date format" {
        Set-Fixture -Changelog "## [1.2.3] - 2026-9-12`n- Invalid date format.`n"
        Assert-Rejected { & $fixtureHelper -RequireReleaseNotes } "must contain exactly one"
    }

    Invoke-Test "extract only exact release section with LF lines" {
        $expectedNotes = "- Current release.`n`n### Details`n`n- Keep nested heading."
        Set-Fixture -Changelog "# Changelog`n`n## Unreleased`n- Future work.`n`n## [1.2.30] - 2026-09-13`n- Similar version.`n`n## [1.2.3] - 2026-09-12`n`n$expectedNotes`n`n## [1.2.2] - 2026-09-01`n- Older release.`n"
        $metadata = & $fixtureHelper -RequireReleaseNotes
        Assert-Equal $metadata.Notes $expectedNotes "exact release notes"
    }

    Invoke-Test "extract final section with CRLF lines" {
        $expectedNotes = "- Current release.`r`n`r`n- Another change."
        Set-Fixture -Changelog "# Changelog`r`n`r`n## [1.2.3] - 2024-02-29`r`n`r`n$expectedNotes`r`n"
        $metadata = & $fixtureHelper -RequireReleaseNotes
        Assert-Equal $metadata.Notes $expectedNotes "final CRLF release notes"
    }

    foreach ($tag in @("v1.2.3", "v1.2.3-rc.1")) {
        Invoke-Test "generate update manifest for $tag" {
            $release = Set-UpdateFixture -Tag $tag
            $null = & $fixtureManifestHelper -ReleaseDirectory $release -Tag $tag -Repository "example/DiscImageStudio"
            $manifestPath = Join-Path $release "latest.json"
            if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
                throw "Update manifest was not written."
            }

            $raw = [IO.File]::ReadAllText($manifestPath)
            if ($raw.Contains("`r")) {
                throw "Update manifest must use LF line endings."
            }
            if (-not $raw.EndsWith("`n")) {
                throw "Update manifest must end with a newline."
            }

            $manifest = $raw | ConvertFrom-Json
            $version = $tag.Substring(1)
            Assert-Equal $manifest.version $version "manifest version"
            Assert-Equal $manifest.tag $tag "manifest tag"
            Assert-Equal $manifest.notesUrl "https://github.com/example/DiscImageStudio/releases/tag/$tag" "manifest notes url"
            Assert-Equal @($manifest.assets.PSObject.Properties).Count 2 "manifest asset count"

            foreach ($rid in 'win-x64', 'win-arm64') {
                $asset = $manifest.assets.$rid
                if ($null -eq $asset) {
                    throw "Manifest is missing the $rid asset."
                }
                Assert-Equal $asset.url "https://github.com/example/DiscImageStudio/releases/download/$tag/DiscImageStudio-$tag-$rid.exe" "$rid asset url"
                if ($asset.sha256 -cnotmatch '^[0-9a-f]{64}$') {
                    throw "$rid asset sha256 is not lowercase hexadecimal: $($asset.sha256)"
                }
                $package = Join-Path $release "DiscImageStudio-$tag-$rid.exe"
                Assert-Equal $asset.sha256 (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() "$rid asset sha256"
                Assert-Equal ([long]$asset.size) ([long](Get-Item -LiteralPath $package).Length) "$rid asset size"
            }
        }
    }

    Invoke-Test "reject update manifest without an arm64 package" {
        $release = Set-UpdateFixture -OmitArm64
        Assert-Rejected { & $fixtureManifestHelper -ReleaseDirectory $release -Tag "v1.2.3" -Repository "example/DiscImageStudio" } "Missing tested package"
    }

    Invoke-Test "reject update manifest for an empty package" {
        $release = Set-UpdateFixture
        $package = Join-Path $release "DiscImageStudio-v1.2.3-win-x64.exe"
        [IO.File]::WriteAllBytes($package, [byte[]]::new(0))
        Assert-Rejected { & $fixtureManifestHelper -ReleaseDirectory $release -Tag "v1.2.3" -Repository "example/DiscImageStudio" } "Tested package is empty"
    }

    Invoke-Test "reject update manifest for a missing release directory" {
        $missing = Join-Path $fixtureRoot "missing-release"
        Assert-Rejected { & $fixtureManifestHelper -ReleaseDirectory $missing -Tag "v1.2.3" -Repository "example/DiscImageStudio" } "Release directory does not exist"
    }

    foreach ($tag in @("1.2.3", "v1.2.3-rc.0", "v1.2.3+build.1")) {
        Invoke-Test "reject update manifest tag $tag" {
            $release = Set-UpdateFixture -Tag "v1.2.3"
            Assert-Rejected { & $fixtureManifestHelper -ReleaseDirectory $release -Tag $tag -Repository "example/DiscImageStudio" } "Tag must be"
        }
    }

    foreach ($repository in @("owner", "owner/name/extra", "owner name/repo")) {
        Invoke-Test "reject update manifest repository $repository" {
            $release = Set-UpdateFixture
            Assert-Rejected { & $fixtureManifestHelper -ReleaseDirectory $release -Tag "v1.2.3" -Repository $repository } "Repository must be owner/name"
        }
    }

    foreach ($serverUrl in @("http://github.com", "https://github.com/", "ftp://github.com")) {
        Invoke-Test "reject update manifest server url $serverUrl" {
            $release = Set-UpdateFixture
            Assert-Rejected { & $fixtureManifestHelper -ReleaseDirectory $release -Tag "v1.2.3" -Repository "example/DiscImageStudio" -ServerUrl $serverUrl } "ServerUrl must be an HTTPS origin"
        }
    }

    Write-Host "release-tooling: all $testCount tests passed"
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot) {
        Assert-ChildPath $fixtureParent (Join-Path $workspaceRoot "artifacts")
        Assert-ChildPath $fixtureRoot $fixtureParent
        Assert-NoReparsePoint $fixtureRoot
        $reparsePoints = @(Get-ChildItem -LiteralPath $fixtureRoot -Recurse -Force |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
        if ($reparsePoints.Count -ne 0) {
            throw "Refusing to remove a test fixture containing a reparse point: $fixtureRoot"
        }
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
