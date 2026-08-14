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
