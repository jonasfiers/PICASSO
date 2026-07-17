# PICASSO

**PICTURE Interpreter for COBOL, Assembling Structured Schemas for OutSystems**

A COBOL copybook parser packaged as an OutSystems Integration Studio Extension. Point it at a `.cpy` file and it derives the byte layout — offsets, lengths, implied decimals, packed-decimal widths, sign placement — then decodes and encodes the fixed-width files that layout describes.

## Why this exists

The OutSystems Forge has no COBOL connector. Mainframe integrations get hand-rolled, every time, by someone reading `PIC` clauses off a copybook and typing byte offsets into a config file by hand.

This repo's sibling project, [CATALOG-74](https://github.com/jonasfiers/CATALOG-74), demonstrates the gap in miniature. Its `api-cobol/lib/specs.js` is exactly that hand transcription:

```js
const BALANCE_SPEC = [
  { name: 'groupId', start: 0, len: 6, type: 'int' },
  { name: 'userId', start: 6, len: 6, type: 'int' },
  { name: 'totalPaid', start: 12, len: 9, type: 'int' },
  // ...
]
```

A human read `BALANCE-REC.cpy`, worked out that `PIC 9(7)V99` occupies nine bytes starting at offset 12, and typed `start: 12, len: 9`. It works. It's also a transcription of information that already exists, in machine-readable form, in the copybook sitting one directory away — and it silently rots the moment someone adds a field.

Nobody wrote the thing that should exist: a parser that reads the actual copybook and derives those offsets itself. That's PICASSO.

The proof is [`ParityWithCatalog74Tests`](test/Picasso.Core.Tests/ParityWithCatalog74Tests.cs): point PICASSO at CATALOG-74's own copybooks and watch it reproduce, byte-for-byte, the exact offsets a human once hand-mirrored.

## What it does

- **Parses** multi-level group/elementary structures, `PIC 9(n)`, `PIC X(n)`, implied decimal (`V`), `SIGN IS LEADING/TRAILING SEPARATE`, and **COMP-3** packed decimal.
- **Derives** the flat byte layout: `{Name, Start, Len, Type, Digits, Scale, Signed, Comp3}` per field.
- **Decodes / encodes** fixed-width flat files against that layout, round-tripping byte-for-byte.
- **Previews** the layout as an OutSystems Structure would see it — `PIC 9(n)` → Integer, implied-decimal and COMP-3 → Decimal, `PIC X(n)` → Text.

It ships ten bundled copybooks (the nine real CATALOG-74 layouts, plus one synthetic) with their seed data, embedded in the assembly.

## The one deliberate discrepancy

PICASSO disagrees with `specs.js` in exactly one place, on purpose.

`BALANCE-REC`'s `NET-BALANCE` is `PIC S9(7)V99 SIGN IS TRAILING SEPARATE`. That clause reserves an extra byte belonging to the *same* data item — ten bytes total: nine digits and a sign character.

`specs.js` splits it into two synthetic fields:

```js
{ name: 'netBalance', start: 30, len: 9, type: 'int' },
{ name: 'sign', start: 39, len: 1, type: 'str' },
```

...because its paired JS codec has no concept of a signed type, which is also why `flatfile.js` needs a bolted-on `signedNetBalance()` helper to glue the two back together at read time.

PICASSO models it the way COBOL defines it: **one** `NET-BALANCE` field spanning bytes 30–39, decoding straight to a signed decimal. No synthetic field, no helper. The parity test asserts this merge explicitly rather than tolerating it as a near-miss — it's the parser getting right what the hand transcription didn't bother with.

## Not supported (v1)

- **`OCCURS`** — repeating groups. A flat `{Start, Len}` list can't express "this 20-byte block, 12 times"; it needs either indexed field names or a nested result shape.
- **`REDEFINES`** — two names for the same bytes. Which is thematically ironic for a project named after a Cubist, and duly noted.
- **Overpunched signs** — `PIC S9(5)` without an explicit `SIGN IS ... SEPARATE` clause is rejected rather than guessed at. Failing loudly beats mis-sizing a field by one byte and corrupting everything downstream of it.

Levels 66 (`RENAMES`) and 88 (condition-names) are rejected with a named error.

## Layout

```
src/
  Picasso.Core/            The engine. netstandard2.0, zero OutSystems dependency.
    Pic.cs                   PIC picture-string parsing
    Comp3.cs                 Packed decimal encode/decode
    CopybookParser.cs        Strip → split → parse → tree → offsets → flatten
    FlatFileCodec.cs         Fixed-width text <-> records
    OutSystemsPreview.cs     Flat layout -> OutSystems attribute rows
    SampleLibrary.cs         The bundled copybooks, embedded in the assembly
    Samples/                 9 real CATALOG-74 copybooks + seed data, 1 synthetic
  Picasso.Extension/       The Integration Studio action surface
    PicassoActions.cs
    README-IntegrationStudio.md
test/
  Picasso.Core.Tests/      Engine tests, including the CATALOG-74 parity golden
  Picasso.Extension.Tests/ The JSON contract, callable without OutSystems
docs/
  outsystems-app-spec.md   The demo app, specified for Service Studio
```

Both `src/` projects target **netstandard2.0** deliberately: it's the one target consumable by plain `dotnet build` here *and* by a .NET Framework 4.7.2+ Integration Studio project on Windows. No dual implementation.

## Building and testing

```bash
dotnet build
dotnet test
```

That's the whole verification loop, and it needs no Windows tooling. The parity test, the roundtrip tests over real seed data, and the full action-surface contract all run here.

## Using it from OutSystems

Integration Studio and Service Studio are proprietary, Windows-only GUI tools. Neither is scriptable, so this repo stops at the last portable artifact: a built, tested pair of DLLs plus the exact wiring steps.

- **[`src/Picasso.Extension/README-IntegrationStudio.md`](src/Picasso.Extension/README-IntegrationStudio.md)** — creating the Extension, defining the five actions, referencing the DLLs, building, importing.
- **[`docs/outsystems-app-spec.md`](docs/outsystems-app-spec.md)** — the demo app's screens, widgets, and Structures.

### The action surface

| Action | Returns |
|---|---|
| `ParseCopybook(copybookSource)` | `flatSpecJson`, `structurePreviewJson` |
| `DecodeRecords(flatSpecJson, fixedWidthText)` | `recordsJson` |
| `EncodeRecords(flatSpecJson, recordsJson)` | `fixedWidthText` |
| `GetSampleCopybook(sampleId)` | `copybookSource`, `sampleDataText` |
| `ListSampleIds()` | JSON array of `{id, filename, description, hasSampleData}` |

Each returns a Boolean success flag plus an `errorMessage`; no exception crosses the boundary. Complex results travel as JSON strings because Integration Studio actions map onto Text/Integer/Decimal/Boolean but not onto arbitrary nested objects — the app deserializes them with OutSystems' built-in `JSONDeserialize`. This is the same pattern real Forge components use when the data shape is dynamic.

## A note on encoding

Every string in the codec is one byte per char (Latin-1), not UTF-8. COMP-3 packs two digits into a byte and the resulting bytes are frequently not valid UTF-8 — read one of these files as UTF-8 and the packed fields are silently destroyed. Callers must read and write with Latin-1.

## License

MIT — see [LICENSE](LICENSE).
