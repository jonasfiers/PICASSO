# Changelog

## 1.0.0 — initial release

- Copybook parser: multi-level group/elementary structures, `PIC 9(n)`, `PIC X(n)`, implied decimal (`V`), `SIGN IS LEADING/TRAILING SEPARATE`, and COMP-3 packed decimal.
- Both COBOL source formats: free-format (`*>` comments) and traditional fixed-format — columns 1–6 sequence numbers stripped, a column-7 `*` treated as a comment line, columns 73–80 (identification area) ignored. Detected per line, so a file can mix both.
- Generic fixed-width codec, decoding/encoding against the derived layout.
- Undelimited fixed-length record files (the mainframe's RECFM=F): records sliced by the layout's own width, with no delimiters between them. Opt-in via `RecordFormat.FixedLength`, or `RecordFormat = "FIXED"` on the `DecodeRecords`/`EncodeRecords` actions; newline-delimited remains the default. Never auto-detected — a delimited file's length can divide evenly by the record length too, so guessing would silently yield misaligned records. The fixed-length path does no newline normalization: in an undelimited file 0x0D and 0x0A are data, and DTAR020.bin really does carry 0x0D inside COMP-3 fields.
- EBCDIC cp037 text support, applied per field so COMP-3 bytes are never translated — a record-level translation would corrupt every packed-decimal field, the same way an FTP ASCII-mode transfer does. Opt-in via `CharacterEncoding.Ebcdic037`, or `TextEncoding = "EBCDIC"` on the `DecodeRecords`/`EncodeRecords` actions; ASCII/Latin-1 remains the default. The cp037 table is generated from .NET's own code page rather than hand-transcribed, and is pinned against real EBCDIC data.
- OutSystems Structure preview (Integer/Decimal/Text attribute rows) from the same layout.
- Integration Studio action surface (`ParseCopybook`, `DecodeRecords`, `EncodeRecords`, `GetSampleCopybook`, `ListSampleIds`).
- 11 bundled sample copybooks (9 real CATALOG-74 layouts, 1 synthetic, 1 genuine 1990s mainframe copybook), embedded in the assembly. None of the data is hand-authored: 7 files are CATALOG-74's own seed data, 2 are real GnuCOBOL output captured by running its batch programs, and 1 is generated through PICASSO's own encoder.
- Golden parity test against CATALOG-74's hand-transcribed `specs.js`, byte-for-byte.
- Roundtrip tests (decode → encode → byte-identical) over every bundled sample, including files written by a real COBOL runtime.
- Real-world validation against DTAR020, a genuine mainframe copybook and its real 379-record extract (not written for this project, sourced from github.com/bmTas/CobolToJson). It is bundled in its original fixed-format form, sequence numbers intact, and its decoded values match independently-published ones exactly. The entire 10,233-byte file — fixed-format copybook, EBCDIC text, packed decimals, no record delimiters — round-trips through the public API to the same bytes it arrived as.
- Not supported yet: `OCCURS`, `REDEFINES`, overpunched (non-separate) signed DISPLAY numerics, headless copy members (no wrapping `01` level), EBCDIC variants other than cp037. DTAR020 still needs a synthetic `01` prepended because of the headless-copy-member gap.
