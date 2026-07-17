# DTAR020 — a real mainframe copybook

Every other bundled sample is either CATALOG-74's own layout or a synthetic one authored for this project. This one isn't either: it's a genuine COBOL copybook, dated 19/12/90, credited to "BRUCE ARTHUR," originally the output of a real reporting system ("DTAB020 FROM THE IML CENTRAL REPORTING SYSTEM," per its own header comment). It's been reused as a teaching/test example across several independent open-source COBOL tools for years — this copy comes from [bmTas/CobolToJson](https://github.com/bmTas/CobolToJson) (LGPL-2.1), including the matching 379-record, 10,233-byte binary data file.

## Files here

- **`DTAR020-ORIGINAL.cbl`** — exactly as downloaded. Traditional fixed-format COBOL source: columns 1–6 are line-sequence numbers, column 7 is the comment indicator.
- **`DTAR020.cpy`** — hand-cleaned for PICASSO: sequence numbers and comment lines stripped, and a synthetic `01 DTAR020-REC.` wrapper added. This is the copybook PICASSO actually parses.
- **`DTAR020.bin`** — the real binary data, unmodified.

## What running the real thing against PICASSO actually found

**The COMP-3 decode is correct.** Once `DTAR020.cpy` exists, PICASSO derives the exact 27-byte layout the copybook's own comment claims ("RECORD LENGTH IS 27"), and decodes every numeric field across all 379 records correctly — including negative packed-decimal values. [`Dtar020RealWorldTests.cs`](../../../../test/Picasso.Core.Tests/Dtar020RealWorldTests.cs) checks this against values independently published in CobolToJson's own README, not anything derived from PICASSO itself.

Two real gaps surfaced that no synthetic copybook had, because synthetic copybooks don't have these problems by construction:

1. **Fixed-format COBOL source isn't handled.** `CopybookParser` assumes free-format source (no sequence-number columns) — every copybook in this repo so far was compiled with `cobc -x -free`. Fed the original file, the parser misread the sequence numbers ("000900", "001000", ...) as level numbers and silently produced one garbage field instead of failing loudly. `DTAR020.cpy` works around this by removing the sequence numbers by hand; the parser itself doesn't strip them.

2. **DTAR020-KEYCODE-NO (the one `PIC X` text field) decodes to garbage: EBCDIC.** The real file encodes text in EBCDIC (cp037, confirmed — its bytes match EBCDIC digit codepoints exactly when the expected value is `"69684558"`), not ASCII. PICASSO only supports ASCII/Latin-1 text. COMP-3 fields are unaffected because packed decimal nibbles aren't a character encoding — only `PIC X` fields are. This is why `DTAR020.bin` isn't bundled as sample data through `GetSampleCopybook`: the demo action surface has no way to decode it correctly yet, and serving it as if it "just works" would be the same silent-wrong-answer failure this project exists to avoid.

Also headless: the original file starts at level `03`, with no wrapping `01` — it's a copy member meant to be `COPY`'d into a program's own record, which is a common, legitimate real-world copybook shape. The synthetic `01` wrapper in `DTAR020.cpy` accounts for this.

Neither gap is fixed here — see the main [README](../../../../README.md)'s "Not supported (v1)" section.
