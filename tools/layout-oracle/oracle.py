#!/usr/bin/env python3
"""
GnuCOBOL differential-offset oracle for PICASSO.

For each copybook in a directory, this compiles it with GnuCOBOL (`cobc`,
configured for IBM/MVS sizing) and reads the *authoritative* byte length of
the whole record via `LENGTH OF`, then diffs that against PICASSO's own computed
record length. GnuCOBOL is a real COBOL compiler, so its sizing is external ground
truth for PICASSO's offset arithmetic — invaluable before implementing an
architectural feature (REDEFINES / multiple-01 / OCCURS DEPENDING ON). (PicassoLayout
also emits a per-field breakdown; the comparison here is on the record total. A
per-field diff is an unimplemented enhancement — see README's "Extending it".)

Requires: `cobc` (GnuCOBOL 3.x) and the .NET SDK on PATH.

Usage:
    python3 oracle.py <copybook-dir>

Known dialect caveat: GnuCOBOL sizes COMP-5 byte-granularly (a 1-2 digit item
is 1 byte); PICASSO uses IBM COMP-5 sizing (2/4/8, same as COMP-4), which is
correct for its mainframe target. So a copybook whose only disagreement is a
1-byte-per-small-COMP-5-field delta is PICASSO being right, not a bug.
"""
import subprocess, re, os, sys, tempfile, glob

HERE = os.path.dirname(os.path.abspath(__file__))

def build_picasso_layout():
    proj = os.path.join(HERE, "PicassoLayout")
    try:
        r = subprocess.run(["dotnet", "build", "-c", "Release", "-v", "q"],
                           cwd=proj, capture_output=True, text=True, timeout=600)
    except subprocess.TimeoutExpired:
        sys.exit("PicassoLayout build timed out (>600s)")
    if r.returncode != 0:
        sys.exit("PicassoLayout build failed:\n" + r.stdout + r.stderr)
    dll = glob.glob(os.path.join(proj, "bin", "Release", "net*", "picasso-layout.dll"))
    if not dll: sys.exit("picasso-layout.dll not found after build")
    return dll[0]

def picasso_totals(dll, files):
    try:
        r = subprocess.run(["dotnet", dll] + files, capture_output=True, text=True, timeout=600)
    except subprocess.TimeoutExpired:
        sys.exit("PicassoLayout dump timed out (>600s)")
    out = {}
    for line in r.stdout.splitlines():
        m = re.match(r'## (.+?) (\d+)$', line)
        if m: out[m.group(1)] = int(m.group(2))
    return out

def _code_completes_stmt(code):
    # True when cols 1-72 already complete a COBOL statement (balanced quotes, ends
    # with the '.' terminator) — so anything in cols 73-80 is the identification area
    # and safe to drop. A free-format line whose real content runs past col 72 has no
    # terminating '.' here, so it returns False and the line is left intact.
    area = code[:72]
    if (area.count("'") + area.count('"')) % 2 != 0:
        return False
    return area.rstrip().endswith('.')

def _seq_cols(line):
    # True when cols 1-6 (indices 0-5) are only digits and spaces with at least one
    # digit — a short or space-padded line number like "00000 " (five zeros + a space),
    # "000010", or "12   ". A letter in cols 1-6 (a free-format DATA-NAME) or an all-
    # blank area (indistinguishable from free-format indentation) is excluded.
    if len(line) < 8:
        return False
    area = line[:6]
    return any(c.isdigit() for c in area) and all(c == ' ' or c.isdigit() for c in area)

def _opens_with_level(code):
    # True when the code area opens with a COBOL level number (1-2 digits, then a
    # blank or end-of-line).
    c = code.lstrip()
    d = 0
    while d < len(c) and c[d].isdigit():
        d += 1
    return 1 <= d <= 2 and (d == len(c) or c[d] == ' ')

def _numeric_spaced_seq(line):
    # A fixed-format line carrying a numeric-spaced sequence area (see _seq_cols) AND
    # the fixed-format signature: col 7 is a '*','-','/' indicator, or is blank/'D' with
    # the code area opening on a level number. A real compiler ignores the sequence
    # area's content, so such a line is legal fixed-format even though it is not six
    # solid digits. The digits/spaces-only test plus the indicator/level guard is what
    # keeps a genuine free-format line (whose cols 1-6 carry the DATA-NAME's letters, or
    # are all-blank indentation) from being misread. Mirrors the parser's
    # HasNumericSpacedSequenceArea; kept an INDEPENDENT reimplementation so the oracle
    # stays at parity with, not weaker than, the reader it checks.
    if not _seq_cols(line):
        return False
    ind = line[6]
    if ind in ('*', '-', '/'):
        return True
    if ind not in (' ', 'D', 'd'):
        return False
    return _opens_with_level(line[7:])

def data_lines(path):
    # NOTE: this is a SEPARATE, independent re-normalization of the copybook from
    # PICASSO's own — deliberately so. The oracle's value is that cobc reaches the
    # layout from a source read by different code than PICASSO's; a bug in either
    # reader then shows up (as a disagreement, or — if this reader is weaker — as an
    # itemized COBC_UNCOMPARABLE, which main() now prints so the gap can't hide).
    # It must stay at least as capable as the parser it checks: hence fixed-format
    # sequence areas and col-7 continuations are handled here too.
    res = []
    pending = None   # the logical line being built; col-7 '-' continuations merge in
    for raw in open(path, encoding='latin-1'):
        line = raw.rstrip("\r\n")
        cont = False
        # Fixed-format with a NUMERIC sequence area (cols 1-6 all digits, e.g.
        # DTAR020's 000100/000200/...): col 7 is the indicator, cols 8-72 the code,
        # cols 73-80 an identification area to drop. Only the numeric-sequence signal
        # is trusted — an all-blank sequence area is indistinguishable from free-format
        # indentation (6 leading spaces before a level number), so those lines are
        # left as-is rather than risk mangling a valid free-format copybook.
        # A numeric-spaced sequence area whose code area does NOT open with a level
        # number, sitting on an OPEN statement (the previous line had no terminating
        # '.'), is an IMPLICIT continuation — e.g. a field's PIC placed on its own line
        # under an OCCURS. Its cols 1-6 must still be stripped or cobc reads the leading
        # sequence digits as garbage code. line[0].isdigit() (a left-aligned sequence
        # number) guards a free-format indented level ("    05 X") from being mis-taken
        # for one; the leftover is emitted as its own line and cobc's free mode joins it
        # onto the open statement across the newline.
        spaced_cont = (not _numeric_spaced_seq(line) and _seq_cols(line)
                       and line[0].isdigit() and line[6] in (' ', 'D', 'd')
                       and not _opens_with_level(line[7:])
                       and pending is not None and not pending.rstrip().endswith('.'))
        if (len(line) >= 7 and line[:6].isdigit()) or _numeric_spaced_seq(line) or spaced_cont:
            if line[6] in ('*', '/'):     # fixed-format comment line
                continue
            cont = (line[6] == '-')       # col-7 continuation of the previous line
            code = line[7:72]             # cols 8-72
        else:
            if line.strip().startswith('*'):   # free-format / area-A comment
                continue
            if len(line) >= 7 and line[6] == '*':
                continue
            code = line
            # Cols 73-80 identification area on a blank-sequence line — the compiler
            # ignores it whatever it holds (a sequence number, a label, a POSITION/
            # offset annotation). Drop it when col 72 is blank and either the tail is
            # digits/spaces or the code area already completes a statement, so a
            # free-format line with real content past col 72 is left intact. Mirrors
            # the parser, keeping this independent reader at parity with it.
            if len(code) > 72 and code[71] == ' ' and (
                    all(c == ' ' or c.isdigit() for c in code[72:])
                    or _code_completes_stmt(code)):
                code = code[:72]
        stripped = code.strip()
        if cont and pending is not None:
            # Continue the previous logical line. If it left a literal open (odd
            # number of quotes), the continuation resumes with a quote — drop that
            # quote and append the rest so the literal closes as one; otherwise a
            # split word/token simply concatenates. Mirrors the parser's handling of
            # the NIST K1WKA continuation shape, so the oracle isn't weaker here.
            open_lit = (pending.count("'") + pending.count('"')) % 2 == 1
            if open_lit and stripped[:1] in ("'", '"'):
                pending += stripped[1:]
            else:
                pending += stripped
            continue
        if not stripped:
            continue
        if pending is not None:
            res.append(pending)
        pending = stripped
    if pending is not None:
        res.append(pending)
    return res

def first_level_and_name(dl):
    for l in dl:
        t = l.split()
        if t and re.fullmatch(r'\d{1,2}', t[0]):
            return int(t[0]), (t[1].rstrip('.') if len(t) > 1 else None)
    return None, None

def gnucobol_total(cpy):
    dl = data_lines(cpy)
    lvl, name = first_level_and_name(dl)
    if lvl is None: return ("ERR", "no data-item lines")
    if lvl == 1:
        rec, decl = name, "\n".join("       " + l for l in dl)
    else:
        rec = "ORACLE-TOP"
        decl = "       01 ORACLE-TOP.\n" + "\n".join("          " + l for l in dl)
    prog = ("       IDENTIFICATION DIVISION.\n       PROGRAM-ID. ORC.\n"
            "       DATA DIVISION.\n       WORKING-STORAGE SECTION.\n"
            f"{decl}\n       PROCEDURE DIVISION.\n"
            f'           DISPLAY "LEN=" LENGTH OF {rec}.\n           STOP RUN.\n')
    with tempfile.TemporaryDirectory() as d:
        src = os.path.join(d, "orc.cob"); open(src, "w").write(prog)
        r = None
        for free in (["-free"], []):  # try free then fixed
            try:
                r = subprocess.run(["cobc", "-x", "-std=mvs", "-o", os.path.join(d, "orc"), src] + free,
                                   capture_output=True, text=True, cwd=d, timeout=60)
            except subprocess.TimeoutExpired:
                return ("ERR", "cobc compile timed out (>60s)")
            if r.returncode == 0:
                try:
                    run = subprocess.run([os.path.join(d, "orc")], capture_output=True, text=True, cwd=d, timeout=15)
                except subprocess.TimeoutExpired:
                    return ("ERR", "compiled program timed out (>15s)")
                m = re.search(r'LEN=\s*0*(\d+)', run.stdout)
                if m: return ("OK", int(m.group(1)))
        # Prefer cobc's actual "error:" line (its stderr sometimes ends on a source
        # echo, not the diagnosis) so the itemized reason in main() is meaningful.
        elines = r.stderr.strip().splitlines() if r and r.stderr else []
        reason = next((l.strip() for l in reversed(elines) if "error:" in l.lower()),
                      elines[-1].strip() if elines else "compile failed")
        return ("ERR", reason[:100])

def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    cbdir = sys.argv[1]
    files = sorted(f for f in glob.glob(os.path.join(cbdir, "**", "*"), recursive=True)
                   if f.lower().endswith(('.cpy', '.cbl', '.cob')))
    if not files: sys.exit(f"no copybooks under {cbdir}")
    dll = build_picasso_layout()
    ptot = picasso_totals(dll, files)
    agree = disagree = err = rejected = 0; mism = []; errs = []
    for f in files:
        if f not in ptot:  # PICASSO rejected/errored on it — not comparable here
            rejected += 1; continue
        status, val = gnucobol_total(f)
        if status != "OK":
            err += 1; errs.append((f, val)); continue
        if val == ptot[f]:
            agree += 1
        else:
            disagree += 1; mism.append((f, ptot[f], val))
    # The four counts add up to the copybook total, so coverage is explicit.
    print(f"AGREE={agree}  DISAGREE={disagree}  COBC_UNCOMPARABLE={err}  PICASSO_REJECTED={rejected}")
    if mism:
        print("\n=== total-length mismatches (PICASSO vs GnuCOBOL) ===")
        for f, p, g in mism: print(f"  {os.path.relpath(f, cbdir)}: PICASSO={p} GnuCOBOL={g} (delta {p-g})")
    if errs:
        # Itemize the uncomparable bucket WITH each cobc reason. A harness/normalization
        # failure (the fixed-format and continuation blind spots both lived here) reads
        # just like a genuine cobc limitation in a bare count — so print the reasons and
        # read them; a low DISAGREE is NOT "all clear" until this list is understood.
        print("\n=== COBC_UNCOMPARABLE — cobc could not build these (reason per file) ===")
        print("    Not automatically PICASSO's fault, and not automatically cobc's:")
        print("    a broken-input harness failure hides here as easily as a real limit.")
        for f, reason in errs: print(f"  {os.path.relpath(f, cbdir)}: {reason}")
    print("\n(See the COMP-5 caveat in this script's docstring before treating a "
          "1-byte delta as a PICASSO defect.)")

if __name__ == "__main__":
    main()
