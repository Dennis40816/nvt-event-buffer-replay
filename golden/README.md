# Golden evidence

Private captures stay outside Git. Record only source provenance, SHA-256,
scoped observations, and which tests were skipped when evidence was absent.

## Common 0x83 NDS parity observation

- Source role: private NDS Communication Log retained in the sibling Python
  reference repository; payload is not copied here.
- SHA-256:
  `0e329071e7c048118054e8db51f6bac3ab6442079556bbc1e358d7100f81d071`
- Operator-selected format: Common `0x83`.
- C# inspection result on 2026-08-21: 518 decoded frames, 518/518 valid CRC8,
  518/518 Host-State-eligible, zero diagnostics. The source contains 518
  `Paint TP 0x01` frame records (the token is the 7-bit I2C slave address) and
  two `Paint TP 0x99000` records (the token is an absolute register address).
  Typed absolute-register evidence uniquely infers IC 51927; no synthetic
  Switch Page transaction is created.
- Python oracle expectation: 518 frames and 518/518 valid CRC8.
- Scope: validates NDS Paint reconstruction, shared finger core, the 0x83 tail,
  CRC parameters, provenance, and CLI streaming. It does not promote 0x82 or
  0x85 tails beyond Provisional evidence.

## Common 0x83 NDS mixed-operation observation

- Source role: private `NT51926TT_CommunicationLog.txt` found in the local
  `nds_helper` example data; payload is not copied into Git.
- SHA-256:
  `d1253e1f938b258d576c354cb7c62f2a16292703dda47adc954cc4485fc47d37`
- Physical shape: 197 `Paint TP 0x02` Event Buffer frames, two absolute
  `Paint TP 0x96A00` records, and explicit Read/Write traffic including
  `0x96A50`, `0x96A60`, `0x96A78`, `0xFF004`, and the small absolute register
  `Write TP 0x62`.
- C# inspection result on 2026-08-21: operator-selected Common `0x83` with
  target slave `0x02` decodes 197/197 CRC-valid and Host-State-eligible frames.
  Absolute Event Buffer evidence at `0x96A00` uniquely infers IC 51926. The six
  emitted diagnostics are informational samples: two FW State `0xA3` reads and
  four TP FW Version reads, all resolved through the shared register catalog.
- Scope: proves that the NDS address token cannot be classified by numeric
  value alone. `Paint` with a 7-bit token is an implicit Event Buffer read;
  explicit Read/Write tokens remain absolute registers even below `0x100`.

## Invalidated Common 0x83 transport observation

- Source role: private decoded-I2C CSV retained in the sibling Python reference
  repository; payload is not copied here. It was initially attributed to
  KingstVIS, but the owner later confirmed that golden attribution/format was
  incorrect.
- SHA-256:
  `4bec1bba2f8a326817fef2baad9f6422fadaa2dbfabe57da0464a3c3a14863e6`
- Physical shape: 2,080 pairs of `0x02 W 0x00` followed by `0x03 R 80 bytes`;
  addresses are the 8-bit write/read forms of TP slave address `0x01`.
- Timing: 17.325286 seconds from first to last read, averaging 119.998 Hz.
- C# inspection result on 2026-08-15: 2,080 decoded Common `0x83` frames,
  2,080/2,080 valid CRC8, with the page intentionally left unknown.
- Scope: the bytes still validate offset-only event-buffer recognition without
  inventing a physical page address and nominal 120 Hz replay. They do not
  validate KingstVIS schema detection, export grouping, or provenance.

## Validated KingstVIS transaction-row observation

- Source role: private KingstVIS decoded-I2C CSV recovered from the owner's
  numbered HackMD transfer on 2026-08-17; payload is not copied here.
- SHA-256:
  `063ad09b6b1a810177ed54b924810963e8a371005768f1642baf40f14a1841e5`
- Schema: `Time[s],Packet ID,Address,Read/Write,Data`; 2,842 sequential rows
  spanning 5.0508599 seconds. Each row is one complete I2C transaction.
- Transport shape: 2,274 writes to 8-bit address `0x02` (1- or 3-byte payloads)
  and 568 reads from `0x03` (80-byte payloads). ACK/NAK is not exported.
- Page shape: `FF 08 08` followed by offset `00` resolves to `0x80800`; the IC
  profile remains an operator choice because that base is shared by two catalog
  profiles.
- C# inspection result: the adapter selects with High confidence and yields
  568/568 CRC-valid frames when Common `0x83` and profile `51929/51932` are
  explicitly selected. The format version and IC profile were validation inputs,
  not inferred from the transport file. Thirteen all-break-payload warnings are
  retained without rewriting the source bytes.

## Validated DSL decoded-I2C observation

- Source role: private DreamLab/libsigrok4DSL decoded-I2C event log retained in
  the sibling Python reference repository; payload is not copied here.
- SHA-256:
  `e7e277fd3ae8445882938b0f9e3b89218e37cbfd78efdd23898719890dbbccb8`
- Schema: `Id,Time[ns],1:I²C: Address/Data`; Start/address/data/ACK/Stop events
  are assembled into 435 complete read transactions.
- Read shape: the 80-byte Common Event Buffer is followed by zero or more raw
  trailing bytes. The normalized source record preserves all bytes; the Common
  decoder consumes the first 80 and exposes the remainder as unconsumed data.
- C# inspection result: 435/435 CRC-valid frames and zero diagnostics when
  Common `0x84` and profile `51929/51932` are explicitly selected.
- Evidence gap: the decoded golden is present locally, but the corresponding
  raw multi-channel DSL waveform named by the Python README is not currently
  present. Automatic SDA/SCL channel discovery therefore remains gated.

## Frozen public regression surface

The private payload is not copied into this repository. Its reviewed parity
counts above are frozen as a provenance observation. Executable regression is
provided by the synthetic/public fixtures under `tests/fixtures`, unit-level
Common and Desay packets, decoded-I2C adapter fixtures, and fixed SHA-256
goldens for deterministic heatmap and replay PNG outputs. Python remains a
historical demo oracle only; all shipped parser, CLI, UI, simulator, analysis,
and release logic is C#.

## 0.0.3 final refactor smoke verification

Re-run on 2026-08-20 after implementation HEAD `022ba60`, using the private
sources in place and without copying them into this repository. The valid
KingstVIS CSV was streamed from the archived HackMD recovery record into an
ignored temporary directory, verified against its SHA-256, inspected, and then
removed. The obsolete sibling-repository CSV remains `4bec1b…` and was not used.

| Evidence | Explicit configuration | Result |
| --- | --- | --- |
| KingstVIS `063ad09…` | adapter `kingstvis-decoded-i2c`; Common `0x83`; IC `51929/51932` | 568 frames, 568 CRC-valid, 568 Host-State-eligible, 13 retained diagnostics |
| DSL `e7e277f…` | adapter `dsl-decoded-i2c`; Common `0x84`; IC `51929/51932` | 435 frames, 435 CRC-valid, 435 Host-State-eligible, zero diagnostics; all 435 retain unconsumed trailing bytes |
| NDS `0e32907…` | adapter `nds-communication-log`; Common `0x83`; IC `51927` | 518 frames, 518 CRC-valid, 518 Host-State-eligible, zero diagnostics |

The obsolete `4bec1b…` transport evidence was also checked only to confirm its
identity; it remains invalid as KingstVIS schema evidence and was not used as
the 0.0.3 KingstVIS gate.

The same verification run also completed a Release solution build with zero
warnings/errors, 289/289 core/parser/rendering/CLI tests, 94/94 Avalonia tests,
and exact comparison of 20 approved UI snapshots. A private Desay Standard/Benz
Palm capture has not been supplied; those paths remain covered by public and
synthetic transaction fixtures and are still an explicit pre-release evidence
gate.
