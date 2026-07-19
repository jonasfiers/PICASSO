# layout-oracle — GnuCOBOL differential offset check

An **external ground-truth check** for PICASSO's byte-layout arithmetic. It
compiles each copybook with **GnuCOBOL** (`cobc`, a real COBOL compiler),
configured for IBM/MVS sizing, reads the authoritative byte length of the record
via `LENGTH OF`, and diffs it against PICASSO's own computed layout.

Why: PICASSO's whole job is getting byte offsets right, and a 1-byte sizing
error silently shifts every field after it. Unit tests and the bundled corpus
catch a lot, but an independent compiler is the strongest check — especially
before implementing an architectural feature (`REDEFINES`, more-than-one-`01`,
`OCCURS ... DEPENDING ON`), where the offset model itself changes.

## Requirements

- **GnuCOBOL** `cobc` 3.x on PATH (`cobc --version`).
- **.NET SDK** on PATH (builds the `PicassoLayout` helper, which references
  `../../src/Picasso.Core`).

## Usage

```
python3 oracle.py <dir-of-copybooks>
```

It parses every `*.cpy` / `*.cbl` / `*.cob` under the directory, and for each
one that **PICASSO parses**, compares total record length against GnuCOBOL:

```
AGREE=57  DISAGREE=0  COBC_UNCOMPARABLE=14  PICASSO_REJECTED=408

=== total-length mismatches (PICASSO vs GnuCOBOL) ===
  jrecord/Numeric.cbl: PICASSO=29 GnuCOBOL=28 (delta 1)
  ...

=== COBC_UNCOMPARABLE — cobc could not build these (reason per file) ===
  ballerinax/copybook-11.cpy: ... error: syntax error, unexpected NO ...
  ...
```

The four counts add up to the copybook total, so coverage is explicit:

- `AGREE` / `DISAGREE` — PICASSO's whole-record length matched / didn't match a
  real compiler. **Trust these:** the two sides read the copybook by independent
  code and compute the layout independently (PICASSO's parser vs cobc), so an
  agreement means two implementations concur.
- `COBC_UNCOMPARABLE` — cobc couldn't build the file. This is now **itemized with
  each cobc error**, on purpose: a genuine cobc limitation (an unsupported
  construct, a `PICTURE clause required`) and a *harness self-failure* (this
  script mis-normalizing its own input) read identically as a bare count — and
  that opacity is exactly what once hid a fixed-format blind spot. Read the
  reasons; a low `DISAGREE` is **not** "all clear" until this list is understood.
- `PICASSO_REJECTED` — PICASSO itself rejected the file (a procedure copybook, an
  unsupported construct, a malformed layout); nothing to compare.

To stay a real check, `oracle.py`'s own copybook normalization is **independent**
of PICASSO's (deliberately — see the note in `data_lines`) but kept at least as
capable: it handles fixed-format numeric sequence areas and col-7 `-` continuation
lines, so it isn't silently weaker than the parser it validates. Every subprocess
(cobc, the compiled probe, the dotnet build/dump) runs under a timeout, so one
pathological file can't wedge a corpus run.

## How it works

1. `PicassoLayout/` — a tiny console (`dotnet`) that prints PICASSO's flat
   layout as TSV (`## <path> <recordLen>` then `name<TAB>start<TAB>len<TAB>type`).
2. `oracle.py` — for each copybook, generates a minimal program that `COPY`s /
   inlines it (headless copy members get wrapped under a synthetic `01`, exactly
   as PICASSO does), compiles with `cobc -x -std=mvs` (IBM binary sizing:
   `COMP` = 2/4/8 bytes, big-endian), runs it, and reads `LENGTH OF` the record.

`-std=mvs` matters: GnuCOBOL's *default* binary sizing is 1/2/4/8 (byte-granular)
and little-endian on x86; `mvs` gives the mainframe 2/4/8 big-endian sizing
PICASSO targets.

**Fixed-format copybooks are handled.** A line whose columns 1–6 are a numeric
sequence area (e.g. DTAR020's `000100`/`000200`/…) is stripped to its code
(cols 8–72, the col-7 indicator honoured) before tokenizing, so a fixed-format
copybook is comparable, not silently dropped into `COBC_UNCOMPARABLE`. (Earlier
this harness compared only free-format copybooks — fixed-format ones failed the
level-number tokenizer and were counted uncomparable, so the flagship DTAR020 was
never actually cross-checked here; it now agrees at 27 bytes. An *all-blank*
sequence area is deliberately left as-is: it's indistinguishable from free-format
indentation, and mis-stripping it would mangle a valid free-format line.)

## Known dialect caveat — COMP-5 (read before filing a "bug")

GnuCOBOL sizes `COMP-5` **byte-granularly** even under `-std=mvs`: a 1–2 digit
`COMP-5` is **1 byte**. PICASSO uses **IBM Enterprise COBOL** `COMP-5` sizing —
the same 2/4/8 widths as `COMP-4`, widening only the value range, not the
storage — which is correct for the IBM-mainframe data PICASSO decodes. So a
copybook whose only disagreement is a **1-byte-per-small-COMP-5-field** delta is
**PICASSO being right for its target**, not a defect. (JRecord ships its COMP-5
test files in `mvs`/`mf`/`fj`/`bs` dialect variants for exactly this reason.)

First run (2026-07-17), against the bundled cobrix + JRecord + AWS-CardDemo
corpus: **74 exact agreements**, 14 one-byte COMP-5 dialect deltas, 20
uncomparable — i.e. PICASSO's sizing matched a real compiler everywhere it was
comparable and same-dialect.

## Extending it

The comparison is on the **whole-record** length. `PicassoLayout` already emits a
per-field breakdown, and the generated program can `DISPLAY LENGTH OF` each field,
so a `--per-field` mode to pinpoint *which* field a delta comes from is a natural
addition — not yet implemented. (The COMP-5 cause above was found by inspecting
those per-field lengths by hand.)
