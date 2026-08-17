# Capture source adapters

Source normalization and Event Buffer decoding are separate decisions. A source
adapter may identify a transport export, but it must never infer Common
`0x82`–`0x85`, Desay `0x97`, or the `0x97` Standard/Benz Palm profile.

## Selection contract

`CaptureSession` probes every built-in adapter, ranks the non-zero results, and
opens a file only when one adapter has the unique highest confidence. A tie is
an operator-decision gate (`SourceSelectionRequiredException`); CLI callers can
resolve it with `--source-adapter <id>`. An explicitly selected adapter must
still accept the file schema.

Every normalized `SourceRecord` retains its stable source ID, line/byte
location where available, original text or row fields, timestamp, direction,
target, register address, declared/actual byte counts, and I2C transport
evidence. Transport evidence includes slave address, preceding write commands,
address ACK when exposed, per-byte ACK/NAK values, and decoder errors.

## Built-in decoded-I2C inputs

| Adapter ID | Input contract |
| --- | --- |
| `saleae-decoded-i2c` | CSV header `Time [s],Packet ID,Address,Data,Read/Write,ACK/NAK`; byte-per-row transactions terminate at NAK. |
| `kingstvis-decoded-i2c` | Validated CSV header `Time[s],Packet ID,Address,Read/Write,Data`; each row is one complete I2C transaction and `Data` contains space-separated `0xNN` bytes. The legacy `Time [s],Packet ID,Address,Data,Read/Write,ACK` byte-per-row dialect remains supported and is grouped by Packet ID. Both paths validate the 8-bit address R/W bit. |
| `dsl-decoded-i2c` | CSV header `Id,Time[ns],1:I²C: Address/Data`; Start, repeated Start, ACK/NAK, and Stop are assembled as one transaction. |
| `acute-decoded-i2c` | Acute `.csv`/`.txt` row-per-transaction report with `Timestamp`, `Status`, `Address(7b/8b/10b)`, and ordered `D0...` columns. English headers, comma/tab/semicolon delimiters, numeric or time-of-day timestamps, direction/address validation, and ACK/NACK suffixes are supported. Ambiguous continuation rows fail closed pending a real export. |
| `excel-decoded-i2c` | `.xlsx`/`.xlsm` worksheet columns `Transaction`, `Start_Time_s`, `Type`, `Byte_Count`, `Bytes_Hex`; rows are streamed from Open XML without Microsoft Excel. The current long-read contract requires leading register-address byte `0x03`, strips it, and maps the payload to `0x99000`. |
| `canonical-i2c-txt` | Versioned JSON Lines beginning with `# NVT-I2C-TXT 1`; accepts the existing Python generator fields and richer transport fields. |
| `nds-communication-log` | NDS Paint/Read/Write text records, including continuation lines. Known addresses are annotated through the shared register catalog and FW Command parser; payload bytes remain immutable. |

Malformed rows and partial transactions create diagnostics and are discarded;
missing canonical timestamps are retained with a warning. The parser never
repairs or mutates captured payload bytes.

## Register tracking

`NvtRegisterTracker` is shared by decoded-I2C adapters. Page command
`FF 09 90 00` selects `0x99000`; a following offset command resolves reads on
that page. Known labels currently include Event Buffer (`+0x00`), FW State
(`+0x60`), frame counter (`+0x70`), DP Version (`+0x76`), and TP FW Version
(`+0x78`). NDS absolute addresses additionally resolve against the built-in IC
profiles. Event Buffer `+0x50` uses a separate FW Command parser (`0x23 =
Baseline Reset` confirmed), and `0xFF0FE ← 0x69` is labeled Software Reset.
Unknown values remain raw. Labels are metadata only.
The identical `0x80800` Event Buffer bases in the 51929/51932 and 51950/51951
profiles are treated as a profile collision, not a shared address. Semantic
register labels at that base require an explicit IC profile.

## Synthetic exports

`DecodedI2cSimulator` emits equivalent Saleae, KingstVIS, DSL, Acute,
canonical TXT, and Excel decoder exports from one `SyntheticI2cTransaction`. Tests feed every
generated export back through its adapter and compare the normalized read,
address, slave, payload, timestamp, and provenance.

The primary KingstVIS contract is based on the owner-supplied export recovered
on 2026-08-17. It records one complete transaction per row, uses 8-bit write/read
addresses such as `0x02`/`0x03`, and does not expose ACK/NAK evidence. The parser
therefore records `ack_evidence=unavailable` rather than inventing acknowledgements.
The earlier byte-per-row v3.5 example remains a compatibility dialect. Any
additional variant still requires a real export and a separate evidence-backed
adapter update.

## Reserved raw-waveform boundary

Raw SDA/SCL waveform decoding is intentionally not part of this milestone.
The Python reference already has validated KingstVIS and DSL waveform readers;
their C# ports remain evidence-gated rather than being reported as decoded-LA
support.
Future waveform adapters implement `ISourceAdapter` and produce the same
`SourceRecord`/`I2cTransport` boundary; they do not bypass source probing or
feed Event Buffer decoders directly. This preserves a single downstream path:

`raw waveform -> decoded I2C adapter -> SourceRecord -> protocol assembler -> Event Buffer decoder`

