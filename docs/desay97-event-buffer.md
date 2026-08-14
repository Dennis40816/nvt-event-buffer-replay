# Desay Event Buffer 0x97 contract

Desay `0x97` is a separate variable-length format, not a Common Event Buffer
version. Decoding requires the operator to select either **Standard** or
**Benz Palm**. Source probing must never infer that choice, and the profile
changes interpretation only; captured bytes remain immutable.

## Two-transaction transport

For NT51927 the Event Buffer base is `0x99000`.

1. Phase one reads exactly one byte from offset `0x00`. Its low nibble is the
   touch count `n`.
2. Phase two starts again at offset `0x00` and reads exactly `1 + 5*n + 1`
   bytes: repeated header, `n` five-byte contacts, then CRC.

Phase two is required when `n=0`; the two-byte header-plus-CRC payload is the
All Break representation. The logical frame uses phase two's timestamp and
retains both physical records and source locations. A missing, orphaned,
duplicate, intervening, wrong-length, header-mismatched, or CRC-invalid pair
is warned and discarded. It never mutates Host State.

This full reread contract reflects the current QA decision and supersedes the
older Python demo's `+0x01` continuation assumption.

## Payload

The header is `Reserved[7] | Short[6] | Open[5] | TP_ASIL_ERR[4] |
NumTouches[3:0]`. Each five-byte contact is:

```text
ProfileFlag[7] | Event_ID[6:5] | Reserved[4] | ID[3:0]
X[15:8] X[7:0] Y[15:8] Y[7:0]
```

`Event_ID` is `0=Invalid`, `1=Enter`, `2=Move`, `3=Break`. In Standard mode
`ProfileFlag` means Palm; in Benz Palm mode it means Invalid. The Benz meaning
remains a documented evidence gate until independently confirmed from a
Benz-specific specification or labeled capture.

CRC-8 uses polynomial `0x1D`, initial value `0x00`, no reflection, and xor-out
`0x00`. It covers the repeated header and contact bytes and excludes the final
CRC byte. The parameter set is inherited from the Python golden reference;
the C# full-reread transport is covered by synthetic fixtures.
