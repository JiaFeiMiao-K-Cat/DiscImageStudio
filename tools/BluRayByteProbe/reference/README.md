# Independent 17PP reference

[Español](README.es.md)

Python standard-library tools transcribed from the user-supplied ISO/IEC 30190:2021, §13.17 and Figure 41. This implementation does not import the production C# encoder. The finite reference was cross-checked against C# for all 256 four-byte repeated inputs and 298 finite bit-pattern vectors; all matched. The expected vectors are included in the output artifacts.

From the repository root:

```powershell
python tools/BluRayByteProbe/reference/analyze.py --output-dir artifacts/bluray-byte-density/reference
python tools/BluRayByteProbe/reference/pattern_search.py --output-dir artifacts/bluray-byte-density/reference
python tools/BluRayByteProbe/reference/robust_pattern_search.py --output-dir artifacts/bluray-byte-density/reference
```

`--output-dir` defaults to the current directory. Both pattern searches accept `--max-period` (default 16). No external Python packages are required.

## Measurement domain

These are **post-scramble inputs at the 17PP input**, before any explicitly modeled DC insertion. They are not raw host-payload patterns. Density is the number of modulation ONEs divided by the number of channel bits: one modulation ONE toggles the NRZI recorded level. Counting changes *between modulation bits* would measure a different quantity.

`analyze.py` computes exact recurrent cycles for every repeated byte and all eight input-bit phases, starting from either preceding modulation bit. It saves byte results as JSON/CSV and four-byte finite vectors. The baseline excludes EDC, RS parity, LDC interleaving, BIS, sync, and DC bits. Fixed-DC results insert a control after each 45 source bits indefinitely; these are sensitivity diagnostics, not legal complete recording frames or DC-balanced outputs.

`pattern_search.py` explicitly evaluates all 131,068 binary strings of lengths 2 through 16, using both possible preceding modulation bits (262,136 exact cycle evaluations). It verifies that density is invariant under rotations by two input bits and under the preceding-bit choice, then groups the 14,694 matching rotation classes. Odd periods have one rotation orbit; even periods retain separate odd/even alignment classes. Equivalent patterns and repeated shorter patterns are kept when reporting per-length extrema, so these are period lengths, not necessarily fundamental periods.

`robust_pattern_search.py` groups full bit rotations and compares both pair phases. Its best phase-robust minimum is 1/6 and maximum is 4/9 **within the searched period lengths**. These two robust results are not asserted as global bounds for longer periods.

NRZI level-one occupancy uses initial level zero; reversing the initial level complements the occupancy. A 50% duty ratio does not predict a black or white optical result.

## Reachable channel-density bounds

For an arbitrarily long 17PP-modulated data segment, each run is at most 8T, hence transition density is at least 1/8. The 16-bit input cycle `0000101101110010` (`0B 72` repeated) attains this bound with output cycle `000100000001000000010000`, three 8T runs.

At the other end, there are at most six consecutive 2T runs. Grouping each sequence of up to six 2T runs with its next run of at least 3T gives density at most 7/15. The 10-bit input cycle `0111011110` (packed as `77 9D E7 79 DE` repeated) attains it: output cycle `101010101001010`, six 2T runs and one 3T run. These are actual table-generated cycles, not merely RLL bounds. Both extrema have 50% NRZI occupancy over their complete level cycles.

These bounds apply to long data segments. Frame sync has special 9T runs and is outside ordinary 17PP rules; finite termination and other physical framing must be counted separately.

| Input pattern | Packing at this phase | Density | Runs in the output cycle | Odd input-bit phase |
|---|---|---:|---|---:|
| `0001` | `11` repeated | 1/6 = 16.6667% | 6T | 1/6 |
| `00111101` | `3D` repeated | 5/12 = 41.6667% | three 2T + two 3T | 5/12 |
| `001111` | `3C F3 CF` repeated | 4/9 = 44.4444% | three 2T + one 3T | 4/9 |
| `0000101101110010` | `0B 72` repeated | 1/8 = 12.5% | three 8T | 7/24 |
| `0111011110` | `77 9D E7 79 DE` repeated | 7/15 = 46.6667% | six 2T + one 3T | 1/3 |

The six-bit high candidate has a bare NRZI duty ratio of 4/9 or 5/9 depending on polarity; DC balancing changes that. Its bit-pair phase robustness makes it a useful next candidate beyond repeated bytes. Blindly inserting DC bits into the two extreme patterns destroys their favorable alignment.

## Maintaining patterns through the physical layout

A useful finer-grained mapping selects each controllable user bit by its final physical position. For pre-DC frame bit index `j`, the number of inserted DC positions before it is `0` when `j < 25`, otherwise `1 + floor((j - 25) / 45)`. Select pattern bit `P[(j + dcBefore(j) + phase) mod period]`, reserving the DC positions rather than allowing their insertion to shift the intended pattern. The actual DC value remains the drive's choice and still perturbs neighboring codewords. Resetting phase at a DC block is a separate strategy worth comparing experimentally.

Construct this sequence in recording-frame/ECC byte order, then use the inverse of the existing LDC permutation to place controllable user bytes in the post-scramble payload. Apply the appropriate scrambler mask to recover host bytes. A byte pattern repeated in host or source-sector order generally does not remain periodic after interleaving. EDC, LDC parity, BIS, sync, and DC values are not independently settable payload bits; include them in full-pipeline measurements rather than treating every frame bit as controllable.

Neither density nor NRZI duty ratio assigns the eventual black/white polarity. Burned-patch reflectance and visibility remain empirical measurements.
