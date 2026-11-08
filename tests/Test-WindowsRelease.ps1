#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$peCheck = Join-Path $workspaceRoot 'packaging\Assert-WindowsReleaseExecutable.ps1'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) "disc-windows-release-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null

function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    try { & $Action | Out-Null }
    catch {
        if (-not $_.Exception.Message.Contains($Message, [StringComparison]::Ordinal)) {
            throw "Expected '$Message', got '$($_.Exception.Message)'."
        }
        return
    }
    throw "Expected rejection containing '$Message'."
}

function Write-PeFixture([string]$Name, [int]$Machine, [uint32]$Flags,
    [switch]$WithoutExtendedCharacteristics, [int]$PayloadSize = 4) {
    # Minimal PE32+ images exercise the real PE reader without executing code.
    $bytes = [byte[]]::new(1024)
    function Set-U16([int]$Offset, [uint16]$Value) {
        [BitConverter]::GetBytes($Value).CopyTo($bytes, $Offset)
    }
    function Set-U32([int]$Offset, [uint32]$Value) {
        [BitConverter]::GetBytes($Value).CopyTo($bytes, $Offset)
    }
    Set-U16 0 0x5A4D
    Set-U32 0x3C 0x80
    Set-U32 0x80 0x4550
    Set-U16 0x84 $Machine
    Set-U16 0x86 1
    Set-U16 0x94 240
    Set-U16 0x96 0x22
    $optional = 0x98
    Set-U16 $optional 0x20B
    Set-U32 ($optional + 32) 4096
    Set-U32 ($optional + 36) 512
    Set-U32 ($optional + 56) 8192
    Set-U32 ($optional + 60) 512
    Set-U32 ($optional + 108) 16
    if (-not $WithoutExtendedCharacteristics) {
        Set-U32 ($optional + 160) 0x1000
        Set-U32 ($optional + 164) 28
    }
    $section = $optional + 240
    Set-U32 ($section + 8) 512
    Set-U32 ($section + 12) 0x1000
    Set-U32 ($section + 16) 512
    Set-U32 ($section + 20) 512
    Set-U32 (512 + 12) 20
    Set-U32 (512 + 16) $PayloadSize
    Set-U32 (512 + 20) (0x1000 + 28)
    Set-U32 (512 + 24) (512 + 28)
    Set-U32 (512 + 28) $Flags
    $path = Join-Path $fixtureRoot $Name
    [IO.File]::WriteAllBytes($path, $bytes)
    return $path
}

try {
    foreach ($case in @(
        @{ Rid = 'win-x64'; Machine = 0x8664 },
        @{ Rid = 'win-arm64'; Machine = 0xAA64 }
    )) {
        $valid = Write-PeFixture "$($case.Rid).exe" $case.Machine 0
        $result = & $peCheck -Path $valid -RuntimeIdentifier $case.Rid
        if ($result.RuntimeIdentifier -ne $case.Rid -or $result.CetCompatible) {
            throw "Incorrect PE validation result for $($case.Rid)."
        }
        $cet = Write-PeFixture "$($case.Rid)-cet.exe" $case.Machine 1
        Assert-Rejected { & $peCheck -Path $cet -RuntimeIdentifier $case.Rid } 'CetCompat=false'
        $otherRid = if ($case.Rid -eq 'win-x64') { 'win-arm64' } else { 'win-x64' }
        Assert-Rejected { & $peCheck -Path $valid -RuntimeIdentifier $otherRid } 'does not match'
        $noEntry = Write-PeFixture "$($case.Rid)-no-entry.exe" $case.Machine 0 -WithoutExtendedCharacteristics
        $null = & $peCheck -Path $noEntry -RuntimeIdentifier $case.Rid
        $otherFlags = Write-PeFixture "$($case.Rid)-other-flags.exe" $case.Machine 2
        $null = & $peCheck -Path $otherFlags -RuntimeIdentifier $case.Rid
    }
    $truncated = Write-PeFixture 'truncated.exe' 0x8664 0 -PayloadSize 2
    Assert-Rejected { & $peCheck -Path $truncated -RuntimeIdentifier win-x64 } 'Invalid extended DLL characteristics'
    $overrun = Write-PeFixture 'overrun.exe' 0x8664 0 -PayloadSize 1024
    Assert-Rejected { & $peCheck -Path $overrun -RuntimeIdentifier win-x64 } 'Invalid extended DLL characteristics'

    # A cross-architecture request must fail before publish or output cleanup.
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        $oppositeRid = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -eq 'Arm64') {
            'win-x64'
        } else { 'win-arm64' }
        Assert-Rejected {
            & (Join-Path $workspaceRoot 'packaging\Build-Release.ps1') -RuntimeIdentifier $oppositeRid
        } 'requires native'
    }
    Write-Host 'Windows release checks passed (x64/ARM64 PE, CET flags, malformed metadata, native-host guard).'
}
finally {
    # The test owns this freshly created, absolute temporary directory.
    $resolvedFixture = [IO.Path]::GetFullPath($fixtureRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedFixture.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolvedFixture) -notlike 'disc-windows-release-*') {
        throw "Refusing to remove an unexpected fixture directory: $resolvedFixture"
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
