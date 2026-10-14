"""Let virglrenderer start on ANGLE (OpenGL ES 3.0 on Direct3D 11).

virglrenderer refuses to start unless the EGL display offers EGL_KHR_surfaceless_context and
EGL_KHR_create_context. The ANGLE library inside the app (from 2016) has neither. But with QEMU the
graphics contexts are made by QEMU itself (on a small off-screen surface), so virglrenderer does not
actually need those two. This removes the check. Run from the folder that contains virgl-src.

It also writes virgl-patch.txt (what it found and changed) so the build can show it as a readable note.
"""
import glob
import os
import sys

MSG = "Missing EGL_KHR_surfaceless_context or EGL_KHR_create_context"
report = []
changed = 0

for path in glob.glob("virgl-src/src/**/*.c", recursive=True):
    text = open(path, encoding="utf-8", errors="replace").read()
    if MSG not in text:
        continue
    pos = 0
    while True:
        idx = text.find(MSG, pos)
        if idx < 0:
            break
        start = text.rfind("if (", 0, idx)
        brace = text.find("{", start)
        report.append("--- %s (message at char %d)" % (path, idx))
        if start < 0 or brace < 0 or brace > idx or "EGL_KHR" not in text[start:brace]:
            report.append("COULD NOT FIND A SIMPLE if(...) {  BEFORE THE MESSAGE. Context:")
            report.append(text[max(0, idx - 700): idx + 200])
            pos = idx + len(MSG)
            continue
        report.append("old condition: " + " ".join(text[start:brace].split()))
        text = text[:start] + "if (0) " + text[brace:]
        changed += 1
        pos = start + 10
    open(path, "w", encoding="utf-8", newline="\n").write(text)

# Context for the next step, in case something else in the start-up needs changing.
for path in ("virgl-src/src/virglrenderer.c", "virgl-src/src/vrend/vrend_winsys.c"):
    if not os.path.exists(path):
        continue
    lines = open(path, encoding="utf-8", errors="replace").read().split("\n")
    for i, l in enumerate(lines):
        if "get_egl_display" in l or "virgl_egl_init" in l or "vrend_winsys_init" in l:
            report.append("%s:%d: %s" % (path, i + 1, l.strip()))

# ---- always-on messages (the renderer's debug switch did not work for us, so print directly) ----
import re as _re

prints = 0
for path in glob.glob("virgl-src/src/vrend/*.c"):
    text = open(path, encoding="utf-8", errors="replace").read()
    new_text, n = _re.subn(r"VREND_DEBUG\(\s*dbg_(object|features)\s*,\s*[A-Za-z_0-9>\-\.\(\)]+\s*,\s*", "virgl_error(", text)
    if n:
        open(path, "w", encoding="utf-8", newline="\n").write(new_text)
        report.append("always-on messages: %s: %d" % (path, n))
        prints += n
report.append("always-on messages total: %d" % prints)
if prints == 0:
    sys.exit("PATCH FAILED: found no VREND_DEBUG(dbg_object/dbg_features) lines to turn on")

# ---- information gathering for the next step (nothing here changes any file) ----
def dump_function(path, signature_start, max_lines=90):
    if not os.path.exists(path):
        report.append("(missing file %s)" % path)
        return
    lines = open(path, encoding="utf-8", errors="replace").read().split("\n")
    for i, l in enumerate(lines):
        if l.startswith(signature_start):
            end = i
            while end < len(lines) and not lines[end].startswith("}") and end - i < max_lines:
                end += 1
            report.append("=== %s:%d %s" % (path, i + 1, signature_start))
            report.extend(lines[i:end + 1])
            return
    report.append("(function %s not found in %s)" % (signature_start, path))


def dump_matches(root, needles, around=4, max_hits=12):
    hits = 0
    for path in glob.glob(root + "/**/*.c", recursive=True) + glob.glob(root + "/**/*.h", recursive=True):
        lines = open(path, encoding="utf-8", errors="replace").read().split("\n")
        for i, l in enumerate(lines):
            if any(n in l for n in needles):
                report.append("=== %s:%d" % (path, i + 1))
                report.extend(lines[max(0, i - around): i + around + 1])
                hits += 1
                if hits >= max_hits:
                    return


# ---- ANGLE / OpenGL ES 3.0 fixes for sampler states (the GPU said "invalid enum" on them) ----
rp = "virgl-src/src/vrend/vrend_renderer.c"
rt = open(rp, encoding="utf-8", errors="replace").read()
fn = "int vrend_create_sampler_state("
a = rt.find(fn)
b = rt.find("ret_handle = vrend_renderer_object_insert", a)
if a < 0 or b < 0:
    sys.exit("PATCH FAILED: sampler state function not found")
body = rt[a:b]
# 1. OpenGL ES 3.0 only knows repeat / clamp-to-edge / mirrored-repeat
body, n1 = _re.subn(r"convert_wrap\(ctx, (templ->wrap_[str])\)", r"uwp_es_wrap(convert_wrap(ctx, \1))", body)
# 2. border colours do not exist on OpenGL ES 3.0
body, n2 = _re.subn(r"(\n\s*)(apply_sampler_border_color\([^;]*;)", r"\1if (!vrend_state.use_gles) \2", body)
# 3. filters are enum values: set them with the integer call
body, n3 = _re.subn(r"glSamplerParameterf\((state->ids\[i\], GL_TEXTURE_M(?:IN|AG)_FILTER)", r"glSamplerParameteri(\1", body)
# 4. after every sampler call, print the GL error and which setting caused it
out = []
for line in body.split("\n"):
    out.append(line)
    m = _re.match(r"\s*glSamplerParameter[if]\(state->ids\[i\], (GL_[A-Z_]+)", line)
    if m and line.rstrip().endswith(";"):
        out.append('         { GLenum e_ = glGetError(); if (e_) virgl_error("UWP sampler call failed: error 0x%x on %s\\n", e_, "' + m.group(1) + '"); }')
body = "\n".join(out)
body = body.replace("glGenSamplers(2, state->ids);", "glGenSamplers(2, state->ids); { GLenum e0_ = glGetError(); if (e0_) virgl_error(\"UWP sampler: error 0x%x was already pending\\n\", e0_); }", 1)
helper = (
    "static GLint uwp_es_wrap(GLint w)\n{\n"
    "   return (w == GL_REPEAT || w == GL_CLAMP_TO_EDGE || w == GL_MIRRORED_REPEAT) ? w : GL_CLAMP_TO_EDGE;\n}\n\n"
)
rt = rt[:a] + helper + body + rt[b:]
open(rp, "w", encoding="utf-8", newline="\n").write(rt)
report.append("sampler patch: wrap %d, border %d, filter %d" % (n1, n2, n3))
if n1 != 3 or n2 != 1 or n3 != 2:
    sys.exit("PATCH FAILED: sampler patch matched %d/%d/%d (expected 3/1/2)" % (n1, n2, n3))

# Sampler code: the GPU refused "CREATE SAMPLER_STATE" with GL error 1280 (invalid enum).
dump_lines = []


def dump_named(names, max_lines=70):
    for path in sorted(glob.glob("virgl-src/src/vrend/*.c")):
        lines = open(path, encoding="utf-8", errors="replace").read().split("\n")
        for i, l in enumerate(lines):
            for n in names:
                if (n + "(") in l and not l.startswith((" ", "\t", "#")) and not l.rstrip().endswith(";"):
                    end = i
                    while end < len(lines) and not lines[end].startswith("}") and end - i < max_lines:
                        end += 1
                    dump_lines.append("=== %s:%d" % (path, i + 1))
                    dump_lines.extend(x.rstrip() for x in lines[i:end + 1] if x.strip())
                    break


dump_named(["vrend_create_sampler_state", "vrend_decode_create_sampler_state", "vrend_create_sampler_view"])
open("virgl-dump.txt", "w").write("\n".join(dump_lines) + "\n")

report.insert(0, "virglrenderer patch: %d check(s) removed" % changed)
open("virgl-patch.txt", "w").write("\n".join(report) + "\n")
print("\n".join(report))
if changed == 0:
    sys.exit("PATCH FAILED: could not find or change the EGL extension check in virglrenderer")
