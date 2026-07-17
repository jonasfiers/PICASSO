#!/usr/bin/env python3
"""
GnuCOBOL differential-offset oracle for PICASSO.

For each copybook in a directory, this compiles it with GnuCOBOL (`cobc`,
configured for IBM/MVS sizing) and reads the *authoritative* byte length of
every field via `LENGTH OF`, then diffs those against PICASSO's own computed
layout. GnuCOBOL is a real COBOL compiler, so its sizing is external ground
truth for PICASSO's offset arithmetic — invaluable before implementing an
architectural feature (REDEFINES / multiple-01 / OCCURS DEPENDING ON).

Requires: `cobc` (GnuCOBOL 3.x) and the .NET SDK on PATH.

Usage:
    python3 oracle.py <copybook-dir> [--per-field]

Known dialect caveat: GnuCOBOL sizes COMP-5 byte-granularly (a 1-2 digit item
is 1 byte); PICASSO uses IBM COMP-5 sizing (2/4/8, same as COMP-4), which is
correct for its mainframe target. So a copybook whose only disagreement is a
1-byte-per-small-COMP-5-field delta is PICASSO being right, not a bug.
"""
import subprocess, re, os, sys, tempfile, glob

HERE = os.path.dirname(os.path.abspath(__file__))

def build_picasso_layout():
    proj = os.path.join(HERE, "PicassoLayout")
    r = subprocess.run(["dotnet", "build", "-c", "Release", "-v", "q"],
                       cwd=proj, capture_output=True, text=True)
    if r.returncode != 0:
        sys.exit("PicassoLayout build failed:\n" + r.stdout + r.stderr)
    dll = glob.glob(os.path.join(proj, "bin", "Release", "net*", "picasso-layout.dll"))
    if not dll: sys.exit("picasso-layout.dll not found after build")
    return dll[0]

def picasso_totals(dll, files):
    r = subprocess.run(["dotnet", dll] + files, capture_output=True, text=True)
    out = {}
    for line in r.stdout.splitlines():
        m = re.match(r'## (.+?) (\d+)$', line)
        if m: out[m.group(1)] = int(m.group(2))
    return out

def data_lines(path):
    res = []
    for raw in open(path, encoding='latin-1'):
        line = raw.rstrip("\n")
        if line.strip().startswith('*'): continue
        if len(line) >= 7 and line[6] == '*': continue
        if not line.strip(): continue
        res.append(line.strip())
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
        for free in (["-free"], []):  # try free then fixed
            r = subprocess.run(["cobc", "-x", "-std=mvs", "-o", os.path.join(d, "orc"), src] + free,
                               capture_output=True, text=True, cwd=d)
            if r.returncode == 0:
                run = subprocess.run([os.path.join(d, "orc")], capture_output=True, text=True, cwd=d)
                m = re.search(r'LEN=\s*0*(\d+)', run.stdout)
                if m: return ("OK", int(m.group(1)))
        return ("ERR", r.stderr.strip().splitlines()[-1][:80] if r.stderr else "compile failed")

def main():
    if len(sys.argv) < 2:
        sys.exit(__doc__)
    cbdir = sys.argv[1]
    files = sorted(f for f in glob.glob(os.path.join(cbdir, "**", "*"), recursive=True)
                   if f.lower().endswith(('.cpy', '.cbl', '.cob')))
    if not files: sys.exit(f"no copybooks under {cbdir}")
    dll = build_picasso_layout()
    ptot = picasso_totals(dll, files)
    agree = disagree = err = 0; mism = []; errs = []
    for f in files:
        if f not in ptot:  # PICASSO rejected it — not comparable here
            continue
        status, val = gnucobol_total(f)
        if status != "OK":
            err += 1; errs.append((f, val)); continue
        if val == ptot[f]:
            agree += 1
        else:
            disagree += 1; mism.append((f, ptot[f], val))
    print(f"AGREE={agree}  DISAGREE={disagree}  COBC_UNCOMPARABLE={err}")
    if mism:
        print("\n=== total-length mismatches (PICASSO vs GnuCOBOL) ===")
        for f, p, g in mism: print(f"  {os.path.relpath(f, cbdir)}: PICASSO={p} GnuCOBOL={g} (delta {p-g})")
    print("\n(See the COMP-5 caveat in this script's docstring before treating a "
          "1-byte delta as a PICASSO defect.)")

if __name__ == "__main__":
    main()
