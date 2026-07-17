# PICASSO

**PICTURE Interpreter for COBOL, Assembling Structured Schemas for OutSystems**

A COBOL copybook parser packaged as an OutSystems Integration Studio Extension. Point it at a `.cpy` file and it derives the byte layout — offsets, lengths, implied decimals, packed-decimal widths, sign placement — then decodes and encodes the fixed-width files that layout describes.

*The .NET implementation was written by Claude (Anthropic), directed and verified throughout by [Jonas Fiers](https://github.com/jonasfiers) — see [Contributions](#contributions).*

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
- **Reads both source formats** — free-format (`*>` comments) and traditional fixed-format, where columns 1–6 are a line-sequence number, column 7 flags a comment, and columns 73–80 are an identification area to ignore. Detection is per line, so the two can mix in one file; no mode flag to pass.
- **Accepts headless copy members** — a copybook with no `01` of its own, written to be `COPY`'d into a record the including program names. PICASSO supplies the `01` COBOL would have gotten from that program, and says it did (`RootIsSynthetic`). Two `01` records in one copybook is a different thing and still refused: those are alternative layouts, not fields of one record.
- **Expands fixed-count `OCCURS`** — a repeating item (`OCCURS n TIMES`, `TIMES` optional) is flattened into `n` 1-indexed fields with sequential offsets, so the flat layout stays flat and every downstream consumer works unchanged. An elementary field becomes `SLOT(1)`…`SLOT(n)`; a group carries the index into every descendant, so `LINE-ITEM OCCURS 3` over `ITEM-CODE`/`ITEM-QTY` gives `LINE-ITEM(1)-ITEM-CODE`, `LINE-ITEM(1)-ITEM-QTY`, `LINE-ITEM(2)-ITEM-CODE`, and so on. The parenthesized index is the whole disambiguation: `(` and `)` are illegal in a COBOL identifier, so an expanded name can never collide with a real one. `INDEXED BY` / `ASCENDING KEY IS` sub-clauses are tolerated and ignored. `OCCURS … DEPENDING ON` and nested `OCCURS` are out of scope — see below.
- **Derives** the flat byte layout: `{Name, Start, Len, Type, Digits, Scale, Signed, Comp3}` per field.
- **Decodes / encodes** fixed-width flat files against that layout, round-tripping byte-for-byte — in ASCII/Latin-1, or in **EBCDIC cp037** for a real mainframe extract. The encoding is applied per field, never to the record as a whole, so COMP-3 bytes are left alone: running packed decimals through a code page table is what destroys them.
- **Reads both record formats** — newline-delimited, or **undelimited fixed-length** (the mainframe's RECFM=F: a bare concatenation with nothing between records, sliced by the layout's own width). Unlike the source format, this is a caller choice rather than detected — see below.
- **Previews** the layout as an OutSystems Structure would see it — `PIC 9(n)` → Integer, implied-decimal and COMP-3 → Decimal, `PIC X(n)` → Text.

It ships eleven bundled copybooks (the nine real CATALOG-74 layouts, one synthetic, and one genuine 1990s mainframe copybook), each with data attached, embedded in the assembly.

None of the data is hand-authored. Seven files are CATALOG-74's own seed data. Two — `AMOUNT-OWED.DAT` and `AMOUNT-PAID.DAT` — are real GnuCOBOL output, captured by compiling CATALOG-74's `CALC-OWED`/`CALC-PAID` and running the batch, which matters because those two layouts have no hand-written `specs.js` counterpart to check against: a real COBOL runtime is the better golden anyway. `PORTRAIT-SAMPLE.DAT` is generated through PICASSO's own encoder rather than typed out, because hand-authoring COMP-3 nibbles is exactly the error-prone transcription this project exists to avoid. And `DTAR020.bin` is a real 1990s mainframe extract, byte-for-byte as it came off the system.

The eleventh, [**DTAR020**](src/Picasso.Core/Samples/dtar020/README.md), isn't CATALOG-74's and isn't synthetic — it's a real copybook from an actual reporting system, dated 19/12/90. Running it as-is is what found the fixed-format-source, EBCDIC, undelimited-record, and headless-copy-member gaps — all four now handled. Both its files are bundled byte-for-byte as downloaded, with nothing adapted for PICASSO: the whole 10,233-byte extract decodes to typed values and re-encodes to the same bytes it arrived as, through nothing but the public API. It's the only sample whose data needs anything other than the default settings, and `GetSampleCopybook` reports the `EBCDIC` and `FIXED` it requires, because neither is detectable from the bytes.

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

- **`OCCURS … DEPENDING ON`** (variable-length / ODO) — `OCCURS m TO n TIMES DEPENDING ON some-field`. Rejected with its own named error, distinct from the fixed-count success path. There's no single fixed count in the clause at all — `m`/`n` are bounds — and the record's length becomes data-dependent at *decode* time, read out of another field per record. PICASSO computes offsets once from the copybook and reuses them across every record; an ODO record can't be expressed that way, so it's a genuinely harder problem left for future work. The `DEPENDING`/`TO` keywords are detected before any count is taken, so ODO never falls through to being mis-read as a fixed count.
- **Nested `OCCURS`** (a table of tables — an `OCCURS` item inside another `OCCURS` item) — rejected with its own named error, a deliberate non-goal for this pass and distinct from ODO. The offset arithmetic generalizes cleanly, but the flattened-name convention (which index binds to which level) and its test surface are a separate design decision; rejected loudly rather than mis-expanded, matching how the parser treats every other unmodeled shape. Fixed-count `OCCURS` at a single level *is* supported — see [What it does](#what-it-does).
- **`REDEFINES`** — two names for the same bytes. Which is thematically ironic for a project named after a Cubist, and duly noted. Rejected explicitly: skipping it used to place the redefining field at the next free offset instead of overlapping the field it redefines, corrupting every offset after it.
- **Overpunched signs** — `PIC S9(5)` without an explicit `SIGN IS ... SEPARATE` clause is rejected rather than guessed at. Failing loudly beats mis-sizing a field by one byte and corrupting everything downstream of it.
- **EBCDIC variants other than cp037** — cp037 (US/Canada) is supported and is the variant DTAR020 uses. cp273 (German), cp500 (International), cp1047 and the rest of the family disagree on punctuation placement, and there's no real file here to verify them against, so they're not guessed at.
- **Binary, float, packed and DBCS `USAGE` clauses** — `COMP`/`COMPUTATIONAL`, `COMP-1`/`COMP-2` (single/double float), `COMP-4`/`COMP-5`/`COMP-6`/`COMP-X`, `BINARY`, `PACKED-DECIMAL`, `SYNC`/`SYNCHRONIZED` (alignment padding), `INDEX`, `POINTER` (and the `POINTER-32`/`POINTER-64`/procedure/function-pointer forms), `OBJECT REFERENCE`, `NATIONAL` and `DISPLAY-1` (double-byte), `UTF-8`, and other binary/float/DBCS/pointer usages are each rejected with a named error. The supported `DISPLAY` sizing is one byte per digit or character; every one of these has a different physical width — a binary halfword/fullword, a 4- or 8-byte float, packed nibbles, an aligned offset — that this parser doesn't compute, so silently skipping the clause would mis-size the field (and `COMP-1`/`COMP-2`, which carry no `PIC`, would drop the field entirely and shift every offset after it). `PACKED-DECIMAL` is a synonym for `COMP-3` but is rejected here rather than aliased to it — decoding it is a separate, deliberate step. `COMP-3` (`COMPUTATIONAL-3`) packed decimal *is* supported — see [What it does](#what-it-does).

Level 66 (`RENAMES`) is rejected with a named error, as are two `01`-level records in one copybook — those are alternative layouts rather than fields of one record, and merging them would describe neither. Level 88 (condition-names) is **tolerated and ignored**: an 88 is metadata on the preceding data item (`88 STATUS-OK VALUE 'A'`) that occupies zero storage, so it's silently dropped — no field, no bytes, no effect on any offset, length, or sibling ordering — the way the parser already ignores `VALUE`/`JUSTIFIED`/`INDEXED BY`. Its body is never interpreted.

## Layout

```
src/
  Picasso.Core/            The engine. netstandard2.0, zero OutSystems dependency.
    Pic.cs                   PIC picture-string parsing
    Comp3.cs                 Packed decimal encode/decode
    Ebcdic.cs                EBCDIC cp037 <-> Unicode, for text bytes only
    Latin1.cs                Byte <-> char, one-for-one and reversibly
    CopybookParser.cs        Strip → split → parse → tree → offsets → flatten
    FlatFileCodec.cs         Fixed-width text <-> records
    OutSystemsPreview.cs     Flat layout -> OutSystems attribute rows
    SampleLibrary.cs         The bundled copybooks, embedded in the assembly
    Samples/                 9 real CATALOG-74 copybooks + seed data, 1 synthetic,
                             1 real mainframe copybook + its real extract, both
                             byte-for-byte as downloaded
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
- **[`docs/forge-submission.md`](docs/forge-submission.md)** — publishing to Forge: listing fields, icon, best-practice checklist, and the O11-vs-ODC compatibility question.
- **[`docs/mainframe-ingestion.md`](docs/mainframe-ingestion.md)** — pulling a real extract off a mainframe via SFTP and decoding it directly, including the `Binary Data` handling this would still need.

### The action surface

| Action | Returns |
|---|---|
| `ParseCopybook(copybookSource)` | `flatSpecJson`, `structurePreviewJson` |
| `DecodeRecords(flatSpecJson, fixedWidthText)` | `recordsJson` |
| `EncodeRecords(flatSpecJson, recordsJson)` | `fixedWidthText` |
| `GetSampleCopybook(sampleId)` | `copybookSource`, `sampleDataText` |
| `ListSampleIds()` | JSON array of `{id, filename, description, hasSampleData}` |
| `DecodeRecordsFromBinary(flatSpecJson, fixedWidthData)` | `recordsJson` |
| `EncodeRecordsToBinary(flatSpecJson, recordsJson)` | `fixedWidthData` |

Each returns a Boolean success flag plus an `errorMessage`; no exception crosses the boundary. Complex results travel as JSON strings because Integration Studio actions map onto Text/Integer/Decimal/Boolean but not onto arbitrary nested objects — the app deserializes them with OutSystems' built-in `JSONDeserialize`. This is the same pattern real Forge components use when the data shape is dynamic.

The last two take/return OutSystems `Binary Data` rather than `Text` — for a caller whose bytes came from an SFTP `Get` or a file upload rather than already being a string. The byte↔char conversion happens inside these actions using the same Latin-1 mapping (`Latin1.cs`) the rest of the codebase already trusts, rather than asking the calling app to convert `Binary Data` to `Text` correctly itself — reaching for OutSystems' default UTF-8 conversion there would silently corrupt COMP-3/EBCDIC bytes. See [`docs/mainframe-ingestion.md`](docs/mainframe-ingestion.md).

## A note on encoding

Every string in the codec is one byte per char (Latin-1), not UTF-8. COMP-3 packs two digits into a byte and the resulting bytes are frequently not valid UTF-8 — read one of these files as UTF-8 and the packed fields are silently destroyed. Callers must read and write with Latin-1.

## Contributions

- **Requirements, direction, and verification** — [Jonas Fiers](https://github.com/jonasfiers). The scope decisions (what's in v1, what's explicitly deferred), the choice to validate against a real mainframe copybook rather than stopping at synthetic test data, and reviewing each change against that real data are his.
- **Implementation** — written by Claude (Anthropic), under that direction throughout, across two machines: a laptop for the initial engine and parser, a homelab devbox for the rest.

Every commit's authorship is visible in the [git history](https://github.com/jonasfiers/PICASSO/commits/main) — this section exists so it's stated plainly rather than left to infer.

## License

MIT — see [LICENSE](LICENSE).
