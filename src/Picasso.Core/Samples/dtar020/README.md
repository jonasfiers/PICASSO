# DTAR020 — a real mainframe copybook

Every other bundled sample is either CATALOG-74's own layout or a synthetic one authored for this project. This one isn't either: it's a genuine COBOL copybook, dated 19/12/90, credited to "BRUCE ARTHUR," originally the output of a real reporting system ("DTAB020 FROM THE IML CENTRAL REPORTING SYSTEM," per its own header comment). It's been reused as a teaching/test example across several independent open-source COBOL tools for years — this copy comes from [bmTas/CobolToJson](https://github.com/bmTas/CobolToJson) (LGPL-2.1), including the matching 379-record, 10,233-byte binary data file.

## Files here

- **`DTAR020-ORIGINAL.cbl`** — exactly as downloaded. Traditional fixed-format COBOL source: columns 1–6 are line-sequence numbers, column 7 is the comment indicator.
- **`DTAR020.cpy`** — byte-for-byte the original, with a synthetic `01 DTAR020-REC.` line prepended and nothing else changed. Sequence numbers and column-7 comment indicators are left exactly as they arrived; the parser handles them. This is the copybook PICASSO actually parses.
- **`DTAR020.bin`** — the real binary data, unmodified.

## What running the real thing against PICASSO actually found

**The COMP-3 decode is correct.** Once `DTAR020.cpy` exists, PICASSO derives the exact 27-byte layout the copybook's own comment claims ("RECORD LENGTH IS 27"), and decodes every numeric field across all 379 records correctly — including negative packed-decimal values. [`Dtar020RealWorldTests.cs`](../../../../test/Picasso.Core.Tests/Dtar020RealWorldTests.cs) checks this against values independently published in CobolToJson's own README, not anything derived from PICASSO itself.

Four real gaps surfaced that no synthetic copybook had, because synthetic copybooks don't have these problems by construction. The first two are now fixed; two remain.

1. **Fixed-format COBOL source wasn't handled — now it is.** `CopybookParser` used to assume free-format source (no sequence-number columns), true of every other copybook here: they're compiled with `cobc -x -free`. Fed the original file, it misread the sequence numbers ("000900", "001000", ...) as level numbers and silently produced one garbage field instead of failing loudly. The parser now detects a fixed-format line by its six leading digits and strips columns 1–6, drops column-7 `*` comment lines, and ignores columns 73–80 — per line, so both formats can mix in one file. `DTAR020.cpy` no longer strips anything by hand, which is what makes it a real test of this.

2. **DTAR020-KEYCODE-NO (the one `PIC X` text field) used to decode to garbage: EBCDIC — now handled.** The real file encodes text in EBCDIC cp037, not ASCII, and PICASSO was Latin-1 only, so `"69684558"` came back as `"öùöøôõõø"`. Passing `CharacterEncoding.Ebcdic037` now decodes it to the value CobolToJson's README publishes, and re-encoding those published values reproduces the real file's 27 bytes exactly.

   The important part is *where* the translation happens: per field, never to the record as a whole. COMP-3 fields are deliberately left untranslated, because packed decimal nibbles aren't a character encoding — running them through a code page table is precisely what corrupts them. That's the same mistake as transferring an extract in FTP ASCII mode, and it's the reason a file carrying COMP-3 has to move in binary mode and therefore arrives with its text still EBCDIC. Only cp037 is supported, and it's named cp037 rather than "EBCDIC" because the family disagrees on punctuation and this is the only variant with a real file here to check against.

3. **It's headless.** The original file starts at level `03`, with no wrapping `01` — it's a copy member meant to be `COPY`'d into a program's own record, which is a common, legitimate real-world copybook shape. The parser requires a single top-level entry, so the synthetic `01` line prepended in `DTAR020.cpy` is still needed.

4. **`DTAR020.bin` has no record delimiters.** It's 10,233 bytes: 379 × 27, a bare concatenation with nothing between records. `FlatFileCodec` splits records on newlines — a Unix convention, not a mainframe one — so it can't chop this file up, and hands it the whole 10,233 bytes as one oversized "record". Decoding a single 27-byte record through the codec works fine; the whole-file tests slice records by offset by hand. This, not EBCDIC, is now the remaining reason `DTAR020.bin` isn't served as selectable sample data through `GetSampleCopybook`.

Gaps 3 and 4 remain open — see the main [README](../../../../README.md)'s "Not supported (v1)" section.
