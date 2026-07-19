# PICASSO demo app — Service Studio spec

A single-screen Reactive Web app that demonstrates the extension end to end: load a copybook, watch PICASSO derive its byte layout, then decode real fixed-width data against that layout and encode it back.

This is a specification, not a file. Service Studio `.oml` modules are only editable inside Service Studio, so this describes what to build rather than shipping it. Everything it depends on — the seven actions and their JSON shapes — is already built and tested in this repo; see [`../src/Picasso.Extension/README-IntegrationStudio.md`](../src/Picasso.Extension/README-IntegrationStudio.md) for getting the extension published first.

## Prerequisite

The `Picasso` extension published, and its seven actions added via **Manage Dependencies**.

---

## Structures

Define these under **Data → Structures**. They mirror the JSON the actions emit; OutSystems' built-in `JSONDeserialize` maps them by name.

### `FieldSpec` — from `FlatSpecJson`

| Attribute | Type |
|---|---|
| `Name` | Text |
| `Start` | Integer |
| `Len` | Integer |
| `Type` | Text |
| `Digits` | Integer |
| `Scale` | Integer |
| `Signed` | Boolean |
| `SignSeparate` | Boolean |
| `SignLeading` | Boolean |
| `Comp3` | Boolean |

`Type` is one of `Text`, `NumericDisplay`, `Comp3`, `Binary`.

**`SignSeparate` and `SignLeading` are load-bearing — do not trim them.** `FlatSpecJson` is an *input* to `DecodeRecords`/`EncodeRecords`, not just something to display. If a round-tripped spec loses those flags, `BALANCE-REC`'s `NET-BALANCE` decodes its trailing `+` as a digit and the call fails. `Comp3` is redundant with `Type = "Comp3"` and exists for convenience.

### `AttributePreview` — from `StructurePreviewJson`

| Attribute | Type |
|---|---|
| `AttributeName` | Text |
| `DataType` | Text |
| `Length` | Integer |
| `Decimals` | Integer |

`DataType` is one of `Text`, `Integer`, `Decimal` — the OutSystems type each COBOL field maps onto.

### `Sample` — from `ListSampleIds`

| Attribute | Type |
|---|---|
| `Id` | Text |
| `Filename` | Text |
| `Description` | Text |
| `HasSampleData` | Boolean |
| `TextEncoding` | Text |
| `RecordFormat` | Text |

`TextEncoding` and `RecordFormat` say how that sample's data must be decoded, in the exact vocabulary `DecodeRecords` accepts — pass them through untouched rather than mapping or assuming. Ten samples report `LATIN1`/`DELIMITED`; `dtar020-rec`, the real mainframe extract, reports `EBCDIC`/`FIXED`. Neither is detectable from the bytes, which is why the sample states them.

---

## Screen: `CopybookExplorer`

### Local variables

| Variable | Type |
|---|---|
| `CopybookSource` | Text |
| `FixedWidthText` | Text |
| `SelectedSampleId` | Text |
| `Samples` | `Sample` List |
| `FlatSpec` | `FieldSpec` List |
| `FlatSpecJson` | Text |
| `StructurePreview` | `AttributePreview` List |
| `RecordsJson` | Text |
| `LayoutView` | Text (`"fields"` or `"structure"`) |
| `Mode` | Text (`"decode"` or `"encode"`) |
| `ErrorMessage` | Text |

Keep `FlatSpecJson` as well as the deserialized `FlatSpec`: the decode/encode actions want the JSON back verbatim, and re-serializing the list is a pointless round trip that risks dropping a flag.

### `OnReady`

1. `ListSampleIds` → `JSONDeserialize` to `Sample` List → `Samples`.
2. Optionally set `SelectedSampleId = "portrait-rec"` and run `LoadSample` — it's the sample that shows off nesting, COMP-3, and a separate sign at once, so the screen looks interesting on arrival.

---

### 1. CopybookPane

| Widget | Binding |
|---|---|
| Dropdown | Source `Samples`, value `Id`, label `Filename`. Bound to `SelectedSampleId`. `OnChange` → `LoadSample`. |
| Text (caption) | `Samples[..].Description` for the selected id — say what's interesting about each one. |
| Input, multi-line | `CopybookSource`. Monospace, ~14 rows. |
| Button "Parse" | → `DoParse` |

Leave the input editable. Watching a hand-typed `PIC` clause change the derived offsets is the demo.

#### Action: `LoadSample`

1. `GetSampleCopybook(SelectedSampleId)`.
2. If not `Success` → `ErrorMessage`, stop.
3. `CopybookSource = CopybookSource` (out), `FixedWidthText = SampleDataText` (out), `TextEncoding = TextEncoding` (out), `RecordFormat = RecordFormat` (out).
4. Call `DoParse`.

Hold `TextEncoding` and `RecordFormat` as local variables and pass them to every `DecodeRecords`/`EncodeRecords` call for that sample. Wiring the four-output `GetSampleCopybook` instead will work for ten of the eleven samples and fail loudly on `dtar020-rec` — deliberately, since it can't report what that one needs.

All eleven bundled samples come with data attached, so this normally populates. Absent data is not an error, though — disable the Run button on `FixedWidthText = ""` rather than treating it as a failure.

#### Action: `DoParse`

1. `ParseCopybook(CopybookSource)`.
2. If not `Success` → set `ErrorMessage`, clear `FlatSpec` / `StructurePreview`, stop.
3. `FlatSpecJson = FlatSpecJson` (out).
4. `JSONDeserialize` `FlatSpecJson` → `FieldSpec` List → `FlatSpec`.
5. `JSONDeserialize` `StructurePreviewJson` → `AttributePreview` List → `StructurePreview`.
6. Clear `ErrorMessage`.

Bind `ErrorMessage` to a Container with `Visible = ErrorMessage <> ""`, styled as an alert. The actions are designed never to throw, so every failure — a bad `PIC` clause, a level 88, a wrong record width — arrives here as readable text. Show it rather than swallowing it.

---

### 2. LayoutTable

A toggle over **two** Table widgets, each `Visible` on `LayoutView`. They're different shapes over different sources, so one table with swapped columns is more trouble than two.

**"Field Layout"** (`LayoutView = "fields"`) — source `FlatSpec`:

| Column | Expression |
|---|---|
| Field | `Name` |
| Start | `Start` |
| Len | `Len` |
| Type | `Type` |
| Dec | `Scale` (blank when `0`) |

**"OutSystems Structure"** (`LayoutView = "structure"`) — source `StructurePreview`:

| Column | Expression |
|---|---|
| Attribute | `AttributeName` |
| Data Type | `DataType` |
| Length | `Length` |
| Decimals | `Decimals` |

This toggle *is* the pitch: the left view is what the copybook says, the right view is the OutSystems Entity you'd otherwise have hand-built. Same information, derived once.

Worth surfacing on `balance-rec`: `NET-BALANCE` is **one** row spanning 10 bytes, not a 9-byte number plus a 1-byte sign. That's the deliberate divergence from CATALOG-74's hand-written `specs.js`, explained in the [README](../README.md).

---

### 3. DataPane

| Widget | Binding |
|---|---|
| Button group / radio | `Mode` — "Decode" or "Encode" |
| Input, multi-line | `FixedWidthText` (Decode) or `RecordsJson` (Encode). Monospace. |
| Button "Run" | → `DoRun`. Disabled when `FlatSpec` is empty. |

Monospace matters here: fixed-width data only reads as fixed-width in a fixed-width font.

#### Action: `DoRun`

- **Decode**: `DecodeRecords(FlatSpecJson, FixedWidthText, TextEncoding, RecordFormat)` → `RecordsJson`. Leave both blank for ASCII, newline-delimited data; pass `EBCDIC` and/or `FIXED` for a cp037 mainframe extract with undelimited fixed-length records.
- **Encode**: `EncodeRecords(FlatSpecJson, RecordsJson)` → `FixedWidthText`.

Either way: if not `Success`, set `ErrorMessage` and stop.

Decode → Encode → Decode should return you to where you started, byte for byte. That round trip over real seed data is what [`RoundtripTests`](../test/Picasso.Core.Tests/RoundtripTests.cs) asserts on every bundled sample.

---

### 4. RecordsTable — the one genuinely hard part

**The problem.** `RecordsJson` has one key per copybook field:

```json
[{"GROUP-ID": 1, "USER-ID": 1, "TOTAL-PAID": 96.00, "NET-BALANCE": 72.00}]
```

Those keys change with every copybook. OutSystems Structures are **static** — `JSONDeserialize` needs a compile-time target — so there is no Structure that deserializes both this and `PORTRAIT-REC`'s eight differently-named fields. This is a real constraint, not a gap in the extension: the payload is dynamic by nature.

Three ways out, worst to best:

#### Option A — Fixed max-column table

Define `RecordRow` with `Col1`…`Col12` Text. Reshape client-side, bind a Table, and set each column's header from `FlatSpec[n].Name` and its `Visible` from `n < FlatSpec.Length`.

Native widgets throughout, but it caps you at 12 fields, and the reshaping still needs JavaScript — so you get the constraint without escaping the dependency. Not recommended.

#### Option B — JavaScript widget (recommended)

Drop a JavaScript node (or an Expression with **Escape Content = No**) that takes `RecordsJson` and `FlatSpecJson` and builds the table itself:

```javascript
const spec = JSON.parse($parameters.FlatSpecJson);
const rows = JSON.parse($parameters.RecordsJson);
const esc = s => String(s).replace(/[&<>"]/g, c =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

const head = spec.map(f => `<th>${esc(f.Name)}</th>`).join('');
const body = rows.map(r =>
    `<tr>${spec.map(f => `<td>${esc(r[f.Name])}</td>`).join('')}</tr>`
).join('');

$parameters.Html = `<table class="picasso-records"><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>`;
```

Iterating `spec` rather than the record's own keys keeps the columns in **byte order** — `Object.keys()` ordering is not something to rely on for a layout whose whole point is byte order.

Escape the values. They're copybook-derived rather than user-authored, but the input box on this very screen is editable, and `Escape Content = No` means whatever comes back is rendered as markup.

This is the honest answer: the data is dynamic, so the rendering is dynamic. ~15 lines and no field cap.

#### Option C — Reshape the JSON server-side (cleanest, needs a sixth action)

The friction is entirely that field names are *keys*. Make them *values* and the shape goes static:

```json
[{"Cells": [{"Name": "GROUP-ID", "Value": "1"}, {"Name": "USER-ID", "Value": "1"}]}]
```

That deserializes into `RecordRow (Cells: RecordCell List)` — fixed shape, any number of fields — and renders with a nested ListRecords: outer over rows, inner over cells. No JavaScript, no column cap, fully native.

The cost is a sixth action (`DecodeRecordsAsCells`) that isn't in the current surface, so it's noted here rather than assumed. If the demo app grows past a proof of concept, this is the change to make.

---

## Suggested layout

```
┌─────────────────────────────┬──────────────────────────────┐
│ CopybookPane                │ LayoutTable                  │
│ ┌─────────────────────────┐ │ [Field Layout][OS Structure] │
│ │ Dropdown: sample        │ │ ┌──────────────────────────┐ │
│ └─────────────────────────┘ │ │ ARTIST-ID    0   6  Int  │ │
│ ┌─────────────────────────┐ │ │ ARTIST-NAME  6  30  Text │ │
│ │ 01 PORTRAIT-REC.        │ │ │ STREET      36  25  Text │ │
│ │   05 ARTIST-ID PIC 9(6).│ │ │ ...                      │ │
│ │   ...                   │ │ └──────────────────────────┘ │
│ └─────────────────────────┘ │                              │
│ [ Parse ]                   │                              │
├─────────────────────────────┴──────────────────────────────┤
│ DataPane            [Decode][Encode]              [ Run ]  │
│ ┌────────────────────────────────────────────────────────┐ │
│ │ 100001PABLO RUIZ PICASSO           7 RUE DES GRANDS... │ │
│ └────────────────────────────────────────────────────────┘ │
├────────────────────────────────────────────────────────────┤
│ RecordsTable                                               │
│ ARTIST-ID │ ARTIST-NAME        │ CITY  │ TOTAL-VALUE       │
│ 100001    │ PABLO RUIZ PICASSO │ PARIS │ 139428500.00      │
└────────────────────────────────────────────────────────────┘
```

Left-to-right, top-to-bottom, the screen reads as the argument: *here is the copybook → here is the layout PICASSO derived from it → here are the bytes → here is typed data.* Nobody transcribed an offset anywhere in that chain.

## Demo path

1. Load **`balance-rec`**. Note `NET-BALANCE`: one 10-byte field, and in the Structure view, one Decimal attribute. CATALOG-74 needs two fields and a helper function for that.
2. Decode. `NET-BALANCE` comes back signed — including the negative rows — with nothing reassembling it.
3. Switch to **`portrait-rec`**. Three-level nesting flattens to byte-ordered leaves; `TOTAL-VALUE` is `PIC S9(9)V99 COMP-3` — 11 digits in **6 bytes**, because packed decimal stores two per byte plus a sign nibble.
4. Decode, then Encode. The bytes come back identical.
5. Edit a `PIC` clause in the copybook box — widen `ARTIST-NAME` to `X(40)` — and re-parse. Every downstream offset moves. That's the entire value proposition in one keystroke: the layout is derived, so it can't drift from the copybook.
