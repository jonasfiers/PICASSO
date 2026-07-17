# Wiring PICASSO into Integration Studio

Everything in this repo is buildable and testable with `dotnet build` / `dotnet test`. The remaining steps are not: Integration Studio is a proprietary, Windows-only GUI tool with no scriptable interface and no project format that can be meaningfully hand-authored outside it. So this repo stops at the last portable artifact — two built, tested DLLs — and this document covers the rest.

The design goal throughout: **Integration Studio should own as little code as possible.** Every action body below is a single delegating line. All the logic, and all the tests, live in `Picasso.Core` and `Picasso.Extension`, where they can be verified without Windows.

## Prerequisites

- Integration Studio (ships with the OutSystems 11 development environment).
- Visual Studio, or another .NET IDE Integration Studio can hand off to.
- The two built DLLs — see below.

## Step 1 — Build the DLLs

On any machine with the .NET SDK:

```bash
dotnet build -c Release
```

This produces, under `netstandard2.0/`:

- `src/Picasso.Core/bin/Release/netstandard2.0/Picasso.Core.dll`
- `src/Picasso.Extension/bin/Release/netstandard2.0/Picasso.Extension.dll`

Both target **netstandard2.0**, which .NET Framework 4.7.2+ consumes directly — that's why this works without a second, Windows-specific implementation.

`Picasso.Core.dll` carries the bundled copybooks and seed data as embedded resources. There are no loose sample files to copy alongside it, and no filesystem paths to configure.

> **`System.Text.Json`.** Both projects depend on it. A netstandard2.0 library consumed by .NET Framework does *not* automatically drag its NuGet dependencies along. If the extension build can't resolve `System.Text.Json` at runtime, add the NuGet package to the Integration Studio-generated project too, and make sure its `System.Text.Json.dll` (plus `System.Memory`, `System.Buffers`, and `System.Runtime.CompilerServices.Unsafe`, which it depends on) end up in the extension's resource list. This is the most likely snag in the whole process.

## Step 2 — Create the Extension

1. Integration Studio → **New Extension**, name it `Picasso`.
2. Save it somewhere sensible; it becomes a `.xif`.

## Step 3 — Define the five actions

Add each action under **Actions** and give it exactly these parameters, in this order.

Every action follows the same convention: a `Success` Boolean output plus an `ErrorMessage` Text output. **No action throws.** A failure is `Success = False` and a populated `ErrorMessage`, which is what makes them safe to call directly from a screen without wrapping every one in exception handling.

All parameters are **Text** unless noted.

### `ParseCopybook`

| Parameter | Direction | Type |
|---|---|---|
| `CopybookSource` | Input | Text |
| `Success` | Output | Boolean |
| `FlatSpecJson` | Output | Text |
| `StructurePreviewJson` | Output | Text |
| `ErrorMessage` | Output | Text |

### `DecodeRecords`

| Parameter | Direction | Type |
|---|---|---|
| `FlatSpecJson` | Input | Text |
| `FixedWidthText` | Input | Text |
| `TextEncoding` | Input | Text |
| `RecordFormat` | Input | Text |
| `Success` | Output | Boolean |
| `RecordsJson` | Output | Text |
| `ErrorMessage` | Output | Text |

`RecordFormat` says how records are separated: blank (or `DELIMITED`) for newline-separated records, or `FIXED` for a bare concatenation of fixed-length records with no delimiters — the mainframe's usual RECFM=F shape. The record *width* always comes from the layout; this only says whether anything sits between one record and the next. Like `TextEncoding`, an unrecognized name comes back as `Success = False`, and it is never auto-detected: a delimited file's length can divide evenly by the record length too, so a guess could silently return misaligned records.

`TextEncoding` names the encoding of the file's **text** bytes: leave it blank (or `LATIN1`/`ASCII`) for ordinary ASCII data, or pass `EBCDIC` for a cp037 mainframe extract. COMP-3 fields are never affected by it. An unrecognized name comes back as `Success = False` rather than quietly defaulting to ASCII. If you'd rather not wire these, there are shorter overloads without them that behave exactly as blank does.

### `EncodeRecords`

| Parameter | Direction | Type |
|---|---|---|
| `FlatSpecJson` | Input | Text |
| `RecordsJson` | Input | Text |
| `TextEncoding` | Input | Text |
| `RecordFormat` | Input | Text |
| `Success` | Output | Boolean |
| `FixedWidthText` | Output | Text |
| `ErrorMessage` | Output | Text |

`TextEncoding` and `RecordFormat` take the same names as `DecodeRecords`: the text bytes are written in that encoding, and `FIXED` writes the records back to back with no delimiters.

### `GetSampleCopybook`

| Parameter | Direction | Type |
|---|---|---|
| `SampleId` | Input | Text |
| `Success` | Output | Boolean |
| `CopybookSource` | Output | Text |
| `SampleDataText` | Output | Text |
| `TextEncoding` | Output | Text |
| `RecordFormat` | Output | Text |
| `ErrorMessage` | Output | Text |

`TextEncoding` and `RecordFormat` report how *this sample's* data must be decoded, in the same vocabulary `DecodeRecords` takes — pass them straight through. Ten samples report `LATIN1`/`DELIMITED`; `dtar020-rec`, the real mainframe extract, reports `EBCDIC`/`FIXED`. They're outputs rather than something you assume because neither is detectable from the bytes: decode that one with the defaults and you get plausible garbage rather than an error.

There's a shorter overload without those two outputs. It works for the ten default samples and deliberately fails on `dtar020-rec` — it can't report what that sample needs, and handing back bytes that decode to garbage unmarked would be worse than refusing.

All eleven bundled samples come with data attached. If a sample ever ships without, `SampleDataText` comes back empty and `Success` is still True — absent data is not a failure. Branch on `HasSampleData` from `ListSampleIds` rather than on an empty string.

### `ListSampleIds`

| Parameter | Direction | Type |
|---|---|---|
| `SampleIdsJson` | Output | Text |

No `Success` flag: the list is compiled into the assembly and has no failure mode.

## Step 4 — Reference the DLLs

**Edit → Resources**, add both:

- `Picasso.Core.dll`
- `Picasso.Extension.dll`

Set each one's **Deploy Action** to `Deploy to Target Directory` so they land in `bin/` on the front-end server.

## Step 5 — Paste the action bodies

**Edit → Source Code (.NET)**. Integration Studio generates a stub class with one method per action and opens it.

The generated names follow OutSystems' convention: methods get an `Mss` prefix, parameters an `ss` prefix. **Match whatever your Integration Studio actually generated** rather than the names below — the convention has been stable for a long time, but the generated stub is the authority, not this document.

Note the shape: generated methods return `void`, with *every* output — including `Success` — as an `out` parameter. `PicassoActions` returns its success flag conventionally, so each body is one line that assigns it.

Add at the top:

```csharp
using Picasso.Extension;
```

Then:

```csharp
private readonly PicassoActions _picasso = new PicassoActions();

public void MssParseCopybook(string ssCopybookSource, out bool ssSuccess,
    out string ssFlatSpecJson, out string ssStructurePreviewJson, out string ssErrorMessage)
{
    ssSuccess = _picasso.ParseCopybook(ssCopybookSource,
        out ssFlatSpecJson, out ssStructurePreviewJson, out ssErrorMessage);
}

public void MssDecodeRecords(string ssFlatSpecJson, string ssFixedWidthText, string ssTextEncoding, string ssRecordFormat,
    out bool ssSuccess, out string ssRecordsJson, out string ssErrorMessage)
{
    ssSuccess = _picasso.DecodeRecords(ssFlatSpecJson, ssFixedWidthText, ssTextEncoding, ssRecordFormat,
        out ssRecordsJson, out ssErrorMessage);
}

public void MssEncodeRecords(string ssFlatSpecJson, string ssRecordsJson, string ssTextEncoding, string ssRecordFormat,
    out bool ssSuccess, out string ssFixedWidthText, out string ssErrorMessage)
{
    ssSuccess = _picasso.EncodeRecords(ssFlatSpecJson, ssRecordsJson, ssTextEncoding, ssRecordFormat,
        out ssFixedWidthText, out ssErrorMessage);
}

public void MssGetSampleCopybook(string ssSampleId, out bool ssSuccess,
    out string ssCopybookSource, out string ssSampleDataText,
    out string ssTextEncoding, out string ssRecordFormat, out string ssErrorMessage)
{
    ssSuccess = _picasso.GetSampleCopybook(ssSampleId,
        out ssCopybookSource, out ssSampleDataText,
        out ssTextEncoding, out ssRecordFormat, out ssErrorMessage);
}

public void MssListSampleIds(out string ssSampleIdsJson)
{
    ssSampleIdsJson = _picasso.ListSampleIds();
}
```

`PicassoActions` holds no mutable state, so a single shared instance is fine.

In the generated project, add references to `Picasso.Core.dll` and `Picasso.Extension.dll` (the same files from Step 4), then build.

## Step 6 — Verify and publish

1. **Verify & Publish** (F5) in Integration Studio.
2. It compiles the .NET project, packages it, and publishes the component to your environment.

If it fails here, it's almost always Step 1's `System.Text.Json` note or a missing DLL reference in the generated project — not the action code, which is one line per method and already covered by `Picasso.Extension.Tests`.

## Step 7 — Import into Service Studio

1. In your app module: **Manage Dependencies** → find `Picasso` → tick the five actions → **Apply**.
2. They appear under the module's Extension actions, callable from any Action flow.

From here, [`docs/outsystems-app-spec.md`](../../docs/outsystems-app-spec.md) specs the demo app: the Structures to define for the JSON payloads, the screen layout, and the widget bindings.

## Sanity check

The fastest end-to-end confirmation, before building any UI:

1. Create a throwaway screen with a button.
2. In its action: `ListSampleIds` → assign the result to a Text local → display it in an Expression.
3. Publish, click.

If you see a JSON array starting `[{"id":"user-rec",...`, then the DLLs deployed, the embedded resources loaded, and the boundary works. Everything after that is data shape.

For a fuller check, `GetSampleCopybook("balance-rec")` → `ParseCopybook` → `DecodeRecords` should hand back records whose `NET-BALANCE` is a signed number — the field CATALOG-74's own JS codec needs a helper to reassemble. That exact sequence is what `Picasso.Extension.Tests` runs on every bundled sample.

## What this repo could not do for you

- **Create the `.xif`.** Integration Studio's extension format is only authorable inside Integration Studio.
- **Name the generated base class.** It's Integration Studio-version specific, which is exactly why `PicassoActions` is a plain class with no OutSystems base type: whatever your IS generates delegates *to* it, so nothing here had to guess.
- **Build the `.oml`.** Same story, one tool over — see the app spec.
