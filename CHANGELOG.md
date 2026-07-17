# Changelog

## 1.0.0 — initial release

- Copybook parser: multi-level group/elementary structures, `PIC 9(n)`, `PIC X(n)`, implied decimal (`V`), `SIGN IS LEADING/TRAILING SEPARATE`, and COMP-3 packed decimal.
- Both COBOL source formats: free-format (`*>` comments) and traditional fixed-format — columns 1–6 sequence numbers stripped, a column-7 `*` treated as a comment line, columns 73–80 (identification area) ignored. Detected per line, so a file can mix both.
- Generic fixed-width codec, decoding/encoding against the derived layout.
- OutSystems Structure preview (Integer/Decimal/Text attribute rows) from the same layout.
- Integration Studio action surface (`ParseCopybook`, `DecodeRecords`, `EncodeRecords`, `GetSampleCopybook`, `ListSampleIds`).
- 11 bundled sample copybooks (9 real CATALOG-74 layouts, 1 synthetic, 1 genuine 1990s mainframe copybook), embedded in the assembly. None of the data is hand-authored: 7 files are CATALOG-74's own seed data, 2 are real GnuCOBOL output captured by running its batch programs, and 1 is generated through PICASSO's own encoder.
- Golden parity test against CATALOG-74's hand-transcribed `specs.js`, byte-for-byte.
- Roundtrip tests (decode → encode → byte-identical) over every bundled sample, including files written by a real COBOL runtime.
- Real-world validation against DTAR020, a genuine mainframe copybook (not written for this project, sourced from github.com/bmTas/CobolToJson): it is bundled in its original fixed-format form, sequence numbers intact, and COMP-3 decode, including negatives, matches independently-published expected values exactly.
- Not supported yet: `OCCURS`, `REDEFINES`, overpunched (non-separate) signed DISPLAY numerics, headless copy members (no wrapping `01` level), EBCDIC text — the last two found via DTAR020, which still needs a synthetic `01` prepended.
