"""Compile one extra C file with exactly the same compiler flags QEMU uses for its own
ui/console.c (same include folders, same defines, same generated headers).
Usage (run inside qemu-src/build after QEMU has been built):
    python compile_like_qemu.py <source.c> <output.o>
"""
import re
import subprocess
import sys

src, out = sys.argv[1], sys.argv[2]
# On Windows meson names object files .obj, on Linux .o
target = "libcommon.a.p/ui_console.c.obj"
if subprocess.call(["ninja", "-t", "targets", "all"], stdout=open("ninja_targets.tmp", "w")) == 0:
    names = [l.split(":")[0] for l in open("ninja_targets.tmp") if l.startswith("libcommon.a.p/ui_console.c.o")]
    if names:
        target = names[0]
lines = subprocess.check_output(["ninja", "-t", "commands", target], text=True).strip().splitlines()
cmd = lines[-1]
# On Windows meson puts quotes around every flag, so allow optional quotes everywhere.
cmd = re.sub(r'\s"?-MD"?\s+"?-MQ"?\s+"?[^\s"]+"?\s+"?-MF"?\s+"?[^\s"]+"?', " ", cmd)
m = re.search(r'\s"?-o"?\s+"?[^\s"]+"?\s+"?-c"?\s+"?[^\s"]+"?\s*$', cmd)
if not m:
    sys.exit("could not find '-o ... -c ...' at the end of the compile command:\n" + cmd[-300:])
# QEMU's own flags make warnings into errors; our file only needs to compile, so switch that off.
cmd = cmd[:m.start()] + ' -Wno-error -Wno-missing-prototypes -o "%s" -c "%s"' % (out, src)
print(cmd[:600] + " ...")
r = subprocess.run(cmd, shell=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
print(r.stdout)
if r.returncode != 0:
    # Also show the first lines as a GitHub annotation, so the error is easy to find.
    msg = r.stdout.strip().replace("%", "%25").replace("\r", "").replace("\n", "%0A")
    print("::error title=uwp_display.c did not compile::" + msg[:1800])
sys.exit(r.returncode)
