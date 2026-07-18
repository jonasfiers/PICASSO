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

- **Parses** multi-level group/elementary structures, `PIC 9(n)`, `PIC X(n)`, implied decimal (`V`), signed DISPLAY in all three forms — `SIGN IS LEADING/TRAILING SEPARATE` (a distinct sign byte) and trailing/leading **overpunch** (`PIC S9(n)` with no `SEPARATE`, the sign folded into a digit's zone nibble, no extra byte) — and **COMP-3** packed decimal (with `PACKED-DECIMAL` accepted as its exact synonym).
- **Decodes binary `COMP` integers** — `COMP`/`COMPUTATIONAL`, `COMP-4`/`COMPUTATIONAL-4`, `COMP-5`/`COMPUTATIONAL-5`, and `BINARY`. These are the single most common non-DISPLAY `USAGE` in real mainframe data. Stored **big-endian, two's-complement** (signed `PIC S9…`) or plain magnitude (unsigned), with the physical width fixed by the PIC digit count the IBM way: 1–4 digits → a 2-byte halfword, 5–9 → a 4-byte fullword, 10–18 → an 8-byte doubleword. An implied decimal (`V`) scales exactly as it does for COMP-3/DISPLAY — the stored integer is value×10^scale. Big-endian only, deliberately (PICASSO decodes mainframe extracts, the same assumption its EBCDIC and COMP-3 paths make); there is no little-endian option. Like COMP-3, these bytes are raw binary and never pass through an encoding table. Binary on an alphanumeric/edited picture, or combined with `SIGN IS … SEPARATE` (the sign is intrinsic to two's complement), is refused. `COMP-5` is sized the **IBM** way — the same 2/4/8-byte widths as `COMP-4`, widening only the value range, not the storage — which is what mainframe data uses; note this differs from Micro Focus / GnuCOBOL "native" `COMP-5`, where a 1–2 digit item is a single byte (a validation run against GnuCOBOL flagged exactly this dialect gap, and PICASSO keeps the IBM sizing on purpose).
- **Inherits group-level `USAGE`** — a `USAGE` clause written on a **group** item (`05 GRP COMP-3.`, or the verbose `USAGE IS COMP-3`) applies to every subordinate elementary item that states no `USAGE` of its own, exactly as COBOL defines it. The inheritable usages are `COMP-3`/`PACKED-DECIMAL`, the binary forms (`COMP`/`COMP-4`/`COMP-5`/`BINARY`), and an explicit `DISPLAY`. So `05 GRP COMP-3.` over `10 A PIC 9(5)` and `10 B PIC 9(7)` makes both children packed decimal — A is 3 bytes, B is 4, the record 7 — not the 12 DISPLAY bytes a naive reading would give (this exact case used to be a **silent miscompute**: a wrong layout with no error). **Nearest ancestor wins**: an elementary item takes the usage of the closest enclosing group that specifies one, a child's own usage overrides any inherited one, and an inner group's usage overrides an outer group's for its subtree. A group with no `USAGE` contributes nothing (children keep `DISPLAY` or their own). If inheritance would hand an item a usage that is unsupported (a group-level `COMP-1`, `POINTER`, …) or illegal for its picture (a group `COMP-3` over an alphanumeric child — a contradiction COBOL itself rejects), it **fails loud** exactly as an explicit such clause does, never a silent mis-size. `SIGN` is deliberately **not** inherited from group level (that is a separate, out-of-scope clause). Every inherited layout is validated field-by-field against GnuCOBOL (`cobc -std=mvs`).
- **Parses edited pictures for their display width** — a numeric- or alphanumeric-edited picture carrying any of `Z * . , / B 0 + - $` or a trailing `CR`/`DB` (or the `P` scaling position), e.g. `zz9`, `-(7)9`, `-ZZZ,ZZZ,ZZZ`, `--,---,---,---,---,--9`, `$$$,$$9.99`. These are the single most common construct in real copybooks that plain `9`/`X` parsing rejects, so each is measured to its exact character width — `V`/`P`/`S` occupy no position, `CR`/`DB` occupy two, every other symbol one, times any `(n)` repeat — and surfaced as a **`Text` field of that width**: byte passthrough that round-trips unchanged. The numeric *value* is deliberately not re-interpreted (no de-editing back to a number, no encoding a number into an edited picture) — width and passthrough only, which is what keeps the following field's offset correct. A genuinely-unknown character still rejects loudly; only the known edit symbols are recognized. `COMP-3` or `SIGN IS … SEPARATE` on an edited picture is nonsensical and refused.
- **Reads both source formats** — free-format (`*>` comments) and traditional fixed-format, where columns 1–6 are a line-sequence number, column 7 flags a comment, and columns 73–80 are an identification area to ignore — dropped even when columns 1–6 are left blank, provided a space gap separates the code from an all-numeric trailing sequence number (so a free-format literal running past column 72 is never truncated). Detection is per line, so the two can mix in one file; no mode flag to pass.
- **Accepts headless copy members** — a copybook with no `01` of its own, written to be `COPY`'d into a record the including program names. PICASSO supplies the `01` COBOL would have gotten from that program, and says it did (`RootIsSynthetic`). Two `01` records in one copybook is a different thing and still refused: those are alternative layouts, not fields of one record.
- **Expands fixed-count `OCCURS`** — a repeating item (`OCCURS n TIMES`, `TIMES` optional) is flattened into `n` 1-indexed fields with sequential offsets, so the flat layout stays flat and every downstream consumer works unchanged. An elementary field becomes `SLOT(1)`…`SLOT(n)`; a group carries the index into every descendant, so `LINE-ITEM OCCURS 3` over `ITEM-CODE`/`ITEM-QTY` gives `LINE-ITEM(1)-ITEM-CODE`, `LINE-ITEM(1)-ITEM-QTY`, `LINE-ITEM(2)-ITEM-CODE`, and so on. The parenthesized index is the whole disambiguation: `(` and `)` are illegal in a COBOL identifier, so an expanded name can never collide with a real one. `INDEXED BY` / `ASCENDING KEY IS` sub-clauses are tolerated and ignored. Nested `OCCURS` is out of scope — see below.
- **Aligns `SYNCHRONIZED`/`SYNC` binary items** — a binary (`COMP`/`COMP-4`/`COMP-5`/`BINARY`) item marked `SYNC` is aligned to its natural byte boundary — a 2-byte halfword to a multiple of 2, a 4-byte fullword to a multiple of 4, an 8-byte doubleword to a multiple of 8 (relative to the start of the record) — by inserting **slack (padding) bytes before it**, which shifts every following field. The slack is surfaced as a synthetic `FILLER-SYNC-<offset>` `Text` field so the flat layout stays contiguous and a SYNC record round-trips **byte-for-byte** (the padding bytes are captured on decode and re-emitted verbatim on encode). `SYNC` on a **non-binary** item (`DISPLAY`, COMP-3, `Text`/edited, or a group) is a no-op, exactly as in IBM COBOL — tolerated, no padding, layout unchanged. Every offset is validated against GnuCOBOL (`cobc -std=mvs`), which sizes and aligns the same way. `SYNC` *inside* an `OCCURS` table is out of scope and rejected loudly — see below.
- **Overlays `REDEFINES`** — a redefining item (`05 B REDEFINES A …`, `FILLER` included) starts at the byte offset of the prior sibling it names, sharing those bytes rather than being appended after them, exactly as COBOL defines it. The target is resolved among prior same-level siblings; a forward reference, a mis-typed name, or a missing target fails loudly rather than guessing an offset. The redefiner may be elementary (its `PIC`) or a group (its sub-fields lay out from the target's offset); several items may redefine one target, each at that offset; a redefinition longer than its target extends the record, a shorter one leaves the following field where the target put it. A group's/record's length is therefore the **maximum** end-offset of its children, not the running sum. **Decode is fully supported** — each overlapping field is an independent reading of the same bytes. **Encode of an overlapping layout is refused, loudly**, naming the overlapping pair: writing two fields that share bytes is ambiguous (the later write silently clobbers the earlier), which is precisely the silent corruption this project exists to prevent — so it is rejected rather than resolved by a last-write-wins guess. Nested `OCCURS` remains out of scope — see below.
- **Decodes variable-length `OCCURS … DEPENDING ON` tables** (ODO) — one *or several* flat `OCCURS m TO n … DEPENDING ON dep` tables per record, each `dep` a field defined *before* its table, and `m` may be `0` (`OCCURS 0 TO n`, a table that can be empty). Because the count is data — read from `dep` per record at decode time — an ODO record has no single fixed layout the way everything above does; PICASSO models it separately. The parse still produces a tree, but marked variable-length (`ParsedCopybook.IsVariableLength`, with one `OdoInfo` per table in `ParsedCopybook.Odos`), and the concrete byte layout is built *per record*. With more than one table there's a catch — a later table's `dep` field sits at an offset that depends on the *earlier* table's count — so the counts are resolved **left-to-right**: fix the earlier counts, which pins the next `dep` field's offset, read it, continue. For each table the codec validates `m ≤ count ≤ n` (out-of-range fails loudly), expands it to that many 1-indexed copies (`TAB(1)…TAB(count)`, or *no* fields at count 0, with everything after shifted to the table's start), and decodes/encodes against the fully-resolved layout — round-tripping byte-for-byte, verified against GnuCOBOL's own record lengths at several count combinations. Everything harder stays rejected, each distinctly: an ODO table nested inside another `OCCURS`, an `OCCURS` (fixed or ODO) nested inside an ODO table, a `dep` that comes after its table or doesn't exist. This lives in the engine (`FlatFileCodec`'s `ParsedCopybook` overloads); the Integration Studio JSON action surface — whose flat spec is static — does not expose it yet and rejects an ODO copybook loudly rather than emit a spec that would silently mis-decode.
- **Derives** the flat byte layout: `{Name, Start, Len, Type, Digits, Scale, Signed, Comp3}` per field.
- **Decodes / encodes** fixed-width flat files against that layout, round-tripping byte-for-byte — in ASCII/Latin-1, or in **EBCDIC cp037** for a real mainframe extract. The encoding is applied per field, never to the record as a whole, so COMP-3 bytes are left alone: running packed decimals through a code page table is what destroys them.
- **Reads both record formats** — newline-delimited, or **undelimited fixed-length** (the mainframe's RECFM=F: a bare concatenation with nothing between records, sliced by the layout's own width). Unlike the source format, this is a caller choice rather than detected — see below.
- **Previews** the layout as an OutSystems Structure would see it — `PIC 9(n)` (DISPLAY, COMP-3, or binary COMP) → Integer, any implied-decimal form → Decimal, `PIC X(n)` → Text.

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

### One byte-round-trip caveat: overpunch alternate positive

Overpunch encode always emits the **preferred** sign — zone C for positive (`{`, `A`…`I`), zone D for negative (`}`, `J`…`R`), the representation real COBOL runtimes write. Some systems, though, store a *positive* value in the **alternate** zone-F form: the plain digits `0`…`9`. PICASSO decodes that form correctly (it's positive), but re-encodes it to the zone-C letter — the same numeric value, a different byte in the sign-carrying position. So a field arriving as `F1 F2 F3` (`+123`, alternate) re-encodes to `F1 F2 C3` (`+123`, preferred). Standard mainframe data uses zone C/D, so byte-for-byte round-trip holds for it; only this one non-standard positive form differs, and it differs in representation only, never in value. (This is character-level identical in Latin-1 and EBCDIC, because cp037 maps `A`→`0xC1`, `J`→`0xD1`, `1`→`0xF1`, and so on — the same table both ways, so a single implementation serves both.)

## Not supported (v1)

- **`OCCURS … DEPENDING ON` beyond flat, top-level tables** — one or several flat ODO tables per record (including `OCCURS 0 TO n`) *are* supported at the engine level ([What it does](#what-it-does)). What stays rejected, each with its own named error: an ODO table **nested inside another `OCCURS`**, an `OCCURS` (fixed or ODO) **nested inside** an ODO table (a table of tables where one is variable), and a **`DEPENDING` field defined after its table or absent** (the count must be readable before that table's section begins). The Integration Studio **JSON action surface** also does not expose ODO — its flat spec is static — so `ParseCopybook` rejects any ODO copybook loudly rather than emit a spec that would silently mis-decode; wiring the variable layout through that boundary is a separate, deliberate follow-up.
- **Nested `OCCURS`** (a table of tables — an `OCCURS` item inside another `OCCURS` item) — rejected with its own named error, a deliberate non-goal for this pass and distinct from ODO. The offset arithmetic generalizes cleanly, but the flattened-name convention (which index binds to which level) and its test surface are a separate design decision; rejected loudly rather than mis-expanded, matching how the parser treats every other unmodeled shape. Fixed-count `OCCURS` at a single level *is* supported — see [What it does](#what-it-does).
- **EBCDIC variants other than cp037** — cp037 (US/Canada) is supported and is the variant DTAR020 uses. cp273 (German), cp500 (International), cp1047 and the rest of the family disagree on punctuation placement, and there's no real file here to verify them against, so they're not guessed at.
- **Float, DBCS and pointer `USAGE` clauses** — `COMP-1`/`COMP-2` (single/double float), `COMP-6`/`COMP-X` (Micro Focus dialect binary), `INDEX`, `POINTER` (and the `POINTER-32`/`POINTER-64`/procedure/function-pointer forms), `OBJECT REFERENCE`, `NATIONAL` and `DISPLAY-1` (double-byte), and `UTF-8` are each rejected with a named error. The supported `DISPLAY` sizing is one byte per digit or character; every one of these has a different physical width — a 4- or 8-byte float, a double-byte character, an implementation-defined pointer — that this parser doesn't compute, so silently skipping the clause would mis-size the field (and `COMP-1`/`COMP-2`, which carry no `PIC`, would drop the field entirely and shift every offset after it). `COMP-1`/`COMP-2` float stays unsupported pending a deliberate binary-floating-point format decision; the Micro Focus `COMP-6`/`COMP-X` are rare enough to defer. The binary integer usages (`COMP`/`COMPUTATIONAL`, `COMP-4`, `COMP-5`, `BINARY`), `PACKED-DECIMAL` (an exact synonym for `COMP-3`), `COMP-3` (`COMPUTATIONAL-3`) packed decimal, and `SYNCHRONIZED`/`SYNC` alignment *are* supported — see [What it does](#what-it-does).
- **`SYNCHRONIZED`/`SYNC` inside an `OCCURS` table** — a SYNC binary item that itself occurs, or sits inside a repeating group, is rejected with a named error. Per-occurrence alignment adds trailing-element slack rules (a table's element stride must stay a multiple of the aligned item's boundary across every occurrence) that this codec does not model. A *flat* SYNC binary item — outside any `OCCURS` — *is* supported; see [What it does](#what-it-does).

Level 66 (`RENAMES`) is rejected with a named error, as are two `01`-level records in one copybook — those are alternative layouts rather than fields of one record, and merging them would describe neither. Level 88 (condition-names) is **tolerated and ignored**: an 88 is metadata on the preceding data item (`88 STATUS-OK VALUE 'A'`) that occupies zero storage, so it's silently dropped — no field, no bytes, no effect on any offset, length, or sibling ordering — the way the parser already ignores `VALUE`/`JUSTIFIED`/`INDEXED BY`. Its body is never interpreted.

## Layout

```
src/
  Picasso.Core/            The engine. netstandard2.0, zero OutSystems dependency.
    Pic.cs                   PIC picture-string parsing
    Comp3.cs                 Packed decimal encode/decode
    Binary.cs                Binary COMP integer encode/decode (big-endian two's complement)
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
