# Register profile file schema

Version `0.0.3` reserves a declarative JSON format for future custom NVT
register profiles. This is a data boundary only: loading a file does **not** add
it to `NvtRegisterCatalog`, select it in the UI, or change decoding. Import and
operator approval remain future work.

## Version 1 contract

`schemaVersion` is currently exactly `1`. Unknown versions and unknown JSON
properties fail closed.

| Field | Contract |
| --- | --- |
| `profileId` | Stable ASCII ID (`A-Z`, `a-z`, `0-9`, `.`, `_`, `-`) |
| `displayName` | Operator-facing name |
| `icFamily` | Runtime IC-family key |
| `icScope` | One or more non-empty IC targets; duplicates are rejected |
| `firmwareScope` | Optional opaque version constraint; v1 stores but does not execute or compare it |
| `addresses` | 24-bit Event Buffer, Common Buffer, and History base addresses |
| `registers` | Declarative region/offset/name/meaning rows |
| `commands` | Declarative region/register-offset/opcode/name/confirmed rows |
| `contentSha256` | Lowercase SHA-256 of the canonical content excluding this field |

Regions are limited to `eventBuffer`, `commonBuffer`, and `history`. A base plus
an offset must remain within the 24-bit NVT address space. Register names are
unique case-insensitively; register offsets are unique within a region. Command
names and `(region, registerOffset, opcode)` keys are also unique. Leading or
trailing whitespace, empty scope entries, duplicate entries, malformed hashes,
and hash/content mismatches are rejected.

Canonical serialization has a fixed property order, sorts IC scope,
registers, and commands, uses UTF-8 without a BOM, and writes one final newline.
Therefore equivalent definition ordering produces identical bytes and the same
content identity.

The schema intentionally has no assembly name, script, callback, plugin, URI,
or executable hook. `System.Text.Json` rejects unmapped properties.

## Headless API reservation

```csharp
var document = NvtRegisterProfileFile.FromBuiltIn(
    NvtRegisterCatalog.FindProfile("51927")!);

await NvtRegisterProfileFile.SaveAsync(path, document, cancellationToken);
var verified = await NvtRegisterProfileFile.LoadAsync(path, cancellationToken);
var runtimeShape = NvtRegisterProfileFile.ToRuntimeProfile(verified);
```

`CreateV1` computes the identity. `SerializeCanonical`, `SaveAsync`,
`LoadAsync`, `Parse`, and `ToRuntimeProfile` all validate before returning or
publishing data. `ToRuntimeProfile` projects only the existing IC family and
three base addresses, so projecting every built-in profile to v1 and back does
not change current register parsing. Register/command definitions are reserved
metadata until a later reviewed activation path is implemented.
