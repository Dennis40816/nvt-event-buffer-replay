# Golden evidence

Private captures stay outside Git. Record only source provenance, SHA-256,
scoped observations, and which tests were skipped when evidence was absent.

## Common 0x83 NDS parity observation

- Source role: private NDS Communication Log retained in the sibling Python
  reference repository; payload is not copied here.
- SHA-256:
  `0e329071e7c048118054e8db51f6bac3ab6442079556bbc1e358d7100f81d071`
- Operator-selected format: Common `0x83`.
- C# inspection result on 2026-08-14: 518 decoded frames, 518/518 valid CRC8,
  518/518 Host-State-eligible, zero diagnostics.
- Python oracle expectation: 518 frames and 518/518 valid CRC8.
- Scope: validates NDS Paint reconstruction, shared finger core, the 0x83 tail,
  CRC parameters, provenance, and CLI streaming. It does not promote 0x82 or
  0x85 tails beyond Provisional evidence.

## Common 0x83 KingstVIS offset-only observation

- Source role: private KingstVIS decoded-I2C CSV retained in the sibling Python
  reference repository; payload is not copied here.
- SHA-256:
  `4bec1bba2f8a326817fef2baad9f6422fadaa2dbfabe57da0464a3c3a14863e6`
- Physical shape: 2,080 pairs of `0x02 W 0x00` followed by `0x03 R 80 bytes`;
  addresses are the 8-bit write/read forms of TP slave address `0x01`.
- Timing: 17.325286 seconds from first to last read, averaging 119.998 Hz.
- C# inspection result on 2026-08-15: 2,080 decoded Common `0x83` frames,
  2,080/2,080 valid CRC8, with the page intentionally left unknown.
- Scope: validates offset-only event-buffer recognition without inventing a
  physical page address, KingstVIS streaming, and nominal 120 Hz replay.

## Frozen public regression surface

The private payload is not copied into this repository. Its reviewed parity
counts above are frozen as a provenance observation. Executable regression is
provided by the synthetic/public fixtures under `tests/fixtures`, unit-level
Common and Desay packets, decoded-I2C adapter fixtures, and fixed SHA-256
goldens for deterministic heatmap and replay PNG outputs. Python remains a
historical demo oracle only; all shipped parser, CLI, UI, simulator, analysis,
and release logic is C#.
