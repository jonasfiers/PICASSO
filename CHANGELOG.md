# Changelog

## 1.0.0 — initial release

- Copybook parser: multi-level group/elementary structures, `PIC 9(n)`, `PIC X(n)`, implied decimal (`V`), `SIGN IS LEADING/TRAILING SEPARATE`, and COMP-3 packed decimal.
- Generic fixed-width codec, decoding/encoding against the derived layout.
- OutSystems Structure preview (Integer/Decimal/Text attribute rows) from the same layout.
- Integration Studio action surface (`ParseCopybook`, `DecodeRecords`, `EncodeRecords`, `GetSampleCopybook`, `ListSampleIds`).
- 10 bundled sample copybooks (9 real CATALOG-74 layouts + 1 synthetic), embedded in the assembly.
- Golden parity test against CATALOG-74's hand-transcribed `specs.js`, byte-for-byte.
- Not supported yet: `OCCURS`, `REDEFINES`, overpunched (non-separate) signed DISPLAY numerics.
