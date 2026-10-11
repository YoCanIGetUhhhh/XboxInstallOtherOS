import os
import sys
from collections import defaultdict

import pefile

exe_path, symbols_file, bin_dir = sys.argv[1], sys.argv[2], sys.argv[3]

allowed = set()
with open(symbols_file, errors="ignore") as f:
    for line in f:
        parts = line.split()
        if not parts:
            continue
        name = parts[-1]
        if name.startswith("__imp_"):
            name = name[6:]
        allowed.add(name)

print("UWP-allowed function names loaded:", len(allowed))
if len(allowed) < 1000:
    print("WARNING: that list is suspiciously small; results below may be wrong.")

local_dlls = {n.lower(): os.path.join(bin_dir, n)
              for n in os.listdir(bin_dir) if n.lower().endswith(".dll")}


def imports_of(path):
    pe = pefile.PE(path, fast_load=True)
    pe.parse_data_directories(directories=[
        pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"],
        pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_DELAY_IMPORT"],
    ])
    found = []
    for attr in ("DIRECTORY_ENTRY_IMPORT", "DIRECTORY_ENTRY_DELAY_IMPORT"):
        for entry in getattr(pe, attr, []):
            dll = entry.dll.decode().lower()
            for imp in entry.imports:
                fn = imp.name.decode() if imp.name else "ordinal#%s" % imp.ordinal
                found.append((dll, fn))
    return found


queue = [exe_path]
seen = set()
blocked = defaultdict(lambda: defaultdict(set))
totals = {}
legacy_crt = defaultdict(set)

while queue:
    path = queue.pop()
    key = os.path.basename(path).lower()
    if key in seen:
        continue
    seen.add(key)
    ok = bad = 0
    for dll, fn in imports_of(path):
        if dll in local_dlls:
            queue.append(local_dlls[dll])
            continue
        if dll.startswith("api-ms-win-crt-") or dll == "ucrtbase.dll":
            ok += 1
            continue
        if dll == "msvcrt.dll":
            legacy_crt[key].add(fn)
            bad += 1
            continue
        if fn in allowed:
            ok += 1
        else:
            blocked[key][dll].add(fn)
            bad += 1
    totals[key] = (ok, bad)

total_bad = sum(len(fns) for d in blocked.values() for fns in d.values())
print()
print("RESULT: %d blocked function imports across %d binaries" % (total_bad, len(totals)))
print()
print("=== Binaries checked (program plus every DLL it needs) ===")
for name in sorted(totals):
    ok, bad = totals[name]
    print("  %-34s allowed %5d   NOT allowed %4d" % (name, ok, bad))

print()
print("=== Functions NOT allowed in UWP, by binary ===")
for name in sorted(blocked):
    print()
    print(name)
    for dll in sorted(blocked[name]):
        fns = sorted(blocked[name][dll])
        print("  from %s (%d):" % (dll, len(fns)))
        for fn in fns:
            print("    " + fn)

if legacy_crt:
    print()
    print("=== Uses the old C runtime (msvcrt.dll), which UWP does not allow ===")
    for name in sorted(legacy_crt):
        print("  %s: %d functions" % (name, len(legacy_crt[name])))

sys.exit(1 if (total_bad or legacy_crt) else 0)
