# Ingesting a real mainframe file directly

PICASSO parses and decodes; it has no network code. This covers the rest of the path — pulling a real fixed-width extract off a mainframe via SFTP and decoding it through PICASSO's actions inside a published OutSystems app.

**The short version:** feasible, and most of it isn't new work. OutSystems Forge already has mature SFTP components; PICASSO's own parsing already proved itself against a real EBCDIC/COMP-3/fixed-length mainframe file ([DTAR020](../src/Picasso.Core/Samples/dtar020/README.md)). The one real gap — how bytes get from an OutSystems Binary Data value into PICASSO without silently corrupting them — is closed: `DecodeRecordsFromBinary`/`EncodeRecordsToBinary` exist for exactly this. Everything from here is app-level wiring, not a PICASSO change.

## Architecture

```
Mainframe (z/OS OpenSSH / SFTP server)
    │  SFTP GET — binary-safe, no EBCDIC/ASCII translation
    ▼
OutSystems SFTP component  →  Binary Data
    │
    ▼
PicassoActions.DecodeRecordsFromBinary(...)
    │
    ▼
Decoded records (JSON) → an OutSystems Structure/RecordList
```

Upload (writing a file back to the mainframe) is the same shape in reverse: build records → `EncodeRecordsToBinary` → the SFTP component's Put action.

## Why SFTP, specifically

Classic FTP has an ASCII transfer mode that z/OS's FTP server uses to auto-translate EBCDIC → ASCII — convenient for a pure-text file, and exactly the kind of silent corruption that would destroy COMP-3 fields in a mixed layout like DTAR020's if someone forgot to force binary/image mode. SFTP has no translation mode at all; it moves bytes, full stop. If the mainframe exposes SFTP (z/OS OpenSSH, common on modern shops), it removes an entire historical class of mainframe-integration bugs rather than just avoiding it by discipline.

## The Binary Data problem — and why the fix belongs in PICASSO

An OutSystems `Get` action hands back `Binary Data`. Getting from there to the `Text` `DecodeRecords` expects today means converting bytes → string — and PICASSO's whole contract (see the README's "A note on encoding") requires that conversion to be one byte per char (Latin-1), never the platform default of UTF-8. Relying on whoever wires up the OutSystems app to remember that, correctly, every time, is the same class of mistake as forgetting FTP binary mode: silent, plausible-looking corruption, discovered only when a packed-decimal field comes out wrong.

The robust fix is to not hand that responsibility to the app at all: `PicassoActions` now has two actions that accept/return `Binary Data` directly, doing the exact byte↔char mapping PICASSO already trusts internally rather than asking OutSystems' generic Text conversion to get it right by accident.

## The actions

```csharp
public bool DecodeRecordsFromBinary(string flatSpecJson, byte[] fixedWidthData,
    string textEncoding, string recordFormat, out string recordsJson, out string errorMessage);

public bool EncodeRecordsToBinary(string flatSpecJson, string recordsJson,
    string textEncoding, string recordFormat, out byte[] fixedWidthData, out string errorMessage);
```

(Each also has the same shorter 2- and 3-argument overloads `DecodeRecords`/`EncodeRecords` already have, defaulting to Latin-1/newline-delimited.)

`byte[]` is what Integration Studio's generated stub uses for the OutSystems `Binary Data` type — same mapping convention the other actions use for `Text`/`Boolean`. Internally, both convert via `Latin1.cs` — the one place that byte↔char mapping lives, shared with `SampleLibrary` rather than duplicated — and then delegate straight to the existing `DecodeRecords`/`EncodeRecords`, so there's exactly one decode/encode implementation underneath both entry points. See [`README-IntegrationStudio.md`](../src/Picasso.Extension/README-IntegrationStudio.md) for the actual Integration Studio wiring.

## The OutSystems app flow

1. Timer, Process, or a button's action calls the chosen SFTP Forge component's Get action (host, credentials, remote path) → `Binary Data`.
2. Call `ParseCopybook` (or `GetSampleCopybook`, if the layout is one of the bundled ones) with the copybook source for this feed. In practice the layout is known ahead of time for a real integration — check the `.cpy` into the app's own resources or a Site Property, don't try to re-derive it from an unknown source at runtime.
3. Call `DecodeRecordsFromBinary(flatSpecJson, binaryData, textEncoding, recordFormat)` → `recordsJson` — `"EBCDIC"`/`"FIXED"` for a typical raw mainframe extract, matching whatever `GetSampleCopybook` reports for `dtar020-rec` if modeling this on that sample.
4. `JSONDeserialize` into a Structure/RecordList shaped for this layout. Same open problem [`outsystems-app-spec.md`](outsystems-app-spec.md) already flags for the demo app: decoded records have a shape that varies per copybook, and OutSystems Structures are fixed-shape — the same answer applies here (a generic Text-keyed structure, or a small JS/Advanced widget, per copybook).
5. Do whatever the integration actually needs with the records (write to an Entity, display, forward on).

## What this repo doesn't solve, and shouldn't try to

- **Dataset naming and generations.** MVS dataset syntax, PDS members, GDG relative-generation resolution — the remote path handed to the SFTP Get action has to already resolve to the exact dataset wanted. That's mainframe-ops knowledge, not something PICASSO or OutSystems can resolve generically.
- **VSAM.** SFTP moves bytes off a dataset; it doesn't understand VSAM's internal structure. A VSAM source needs an unload/copy-to-physical-sequential step on the mainframe side first — same as it always has, independent of PICASSO.
- **Network access and credentials.** Whether the OutSystems environment can even reach the mainframe's SFTP port, and how the service account/key is stored (OutSystems Site Properties marked Encrypted, at minimum, or a real secrets manager if the org has one) is a security and infrastructure decision for whoever owns that network path — not a code problem this repo can settle.
- **Scheduling.** Mainframe batch windows mean files often land at a specific time (compare CATALOG-74's own `BATCH_INTERVAL_MS` timer for the same reason). A Timer polling on a matching schedule is the realistic pattern; an on-demand button is fine for a demo but not for a production feed.
- **Failure handling.** Every PICASSO action already fails loudly (`Success = False` plus `ErrorMessage`, never an exception across the boundary) specifically so a caller can branch and alert rather than silently continuing on partial or wrong data. The app flow should actually check `Success` at every step here — a corrupted transfer or a wrong GDG generation should stop the flow, not produce plausible-looking garbage records downstream.
