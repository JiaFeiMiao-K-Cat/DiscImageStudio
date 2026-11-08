#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$RuntimeIdentifier
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedMachine = if ($RuntimeIdentifier -eq 'win-arm64') { 0xAA64 } else { 0x8664 }
$stream = [IO.File]::OpenRead([IO.Path]::GetFullPath($Path))
$pe = $null
try {
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    $actualMachine = [int]$pe.PEHeaders.CoffHeader.Machine
    if ($null -eq $pe.PEHeaders.PEHeader -or
        $pe.PEHeaders.PEHeader.Magic -ne [Reflection.PortableExecutable.PEMagic]::PE32Plus) {
        throw "Release executable must be a PE32+ image: $Path"
    }
    if ($actualMachine -ne $expectedMachine) {
        throw ('Release executable machine 0x{0:X4} does not match {1} (0x{2:X4}): {3}' -f
            $actualMachine, $RuntimeIdentifier, $expectedMachine, $Path)
    }

    # CET_COMPAT is bit 0 of IMAGE_DEBUG_TYPE_EX_DLLCHARACTERISTICS (20),
    # not a bit in the ordinary optional-header DllCharacteristics field.
    foreach ($entry in $pe.ReadDebugDirectory()) {
        if ([int]$entry.Type -ne 20) { continue }
        if ($entry.DataSize -lt 4 -or $entry.DataPointer -lt 0 -or
            [long]$entry.DataPointer + $entry.DataSize -gt $stream.Length) {
            throw "Invalid extended DLL characteristics in release executable: $Path"
        }
        $stream.Position = $entry.DataPointer
        $flags = [byte[]]::new(4)
        $stream.ReadExactly($flags, 0, $flags.Length)
        if (([BitConverter]::ToUInt32($flags, 0) -band 1) -ne 0) {
            throw "Release executable is CET-compatible; publish with CetCompat=false: $Path"
        }
    }

    [pscustomobject]@{
        Path = [IO.Path]::GetFullPath($Path)
        RuntimeIdentifier = $RuntimeIdentifier
        Machine = ('0x{0:X4}' -f $actualMachine)
        CetCompatible = $false
    }
}
finally {
    if ($null -ne $pe) { $pe.Dispose() }
    $stream.Dispose()
}
