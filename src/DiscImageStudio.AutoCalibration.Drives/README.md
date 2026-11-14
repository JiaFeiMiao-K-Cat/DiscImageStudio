# Optical-drive adapters

This project provides direct SCSI/MMC pass-through for automatic calibration:

- Windows: SPTI using `IOCTL_SCSI_PASS_THROUGH_DIRECT`.
- Linux: SG_IO using `ioctl(SG_IO)` on `/dev/sr*`.
- macOS: the reduced direct-I/O fallback enumerates whole `IOMedia` services and
  verifies an optical-media class (`IOCDMedia`, `IODVDMedia`, or `IOBDMedia`)
  before opening `/dev/rdiskN` read-only with `F_NOCACHE`. It does not implement
  `SCSITaskUserClient` pass-through. Empty optical drives without an `IOMedia`
  node are therefore not listed.

Timed reads issue one MMC READ(12) for a 2048-byte user sector. Timing uses the
monotonic `Stopwatch` clock and records the completion timestamp after the
pass-through returns. Cancellation is checked before and after synchronous
kernel calls; it cannot abort an already-running ioctl. The drivers use a
30-second command timeout. Like Discratcher, Windows and Linux use FUA on timed
READ(12) commands to avoid an extra distant-sector read before every sample. If a
drive rejects FUA, the SCSI error is surfaced rather than silently switching to a
different sampling path.
Raw-sector reads use MMC READ CD and return `false` only when the device reports
that the command/opcode or field is unsupported. On macOS, `F_NOCACHE` only
skips the host cache: the drive's internal cache remains active, FUA and raw
sector reads are unavailable, and SET CD SPEED is a no-op. Media metadata is
limited to IOKit properties; disc blankness and exact recording profile cannot
be determined by this fallback. The adapter never unmounts media or invokes
`diskutil`/`drutil`.
