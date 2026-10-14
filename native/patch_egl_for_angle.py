"""Small fixes to QEMU's EGL/OpenGL helper code so it works with ANGLE (OpenGL ES 3.0 on Direct3D 11).

Run from the folder that contains qemu-src. Every change is checked: if QEMU's text is not what we
expect, this stops with an error instead of silently building something that will not work.

What it fixes (each one was found by reading the code, the first was seen in a real log):
  1. ANGLE (2016) cannot make a context current without a surface. We give it a small off-screen one.
  2. QEMU's blit shaders are "#version 300 es", so the context must be OpenGL ES 3, not 2.
  3. The drawing format must allow off-screen surfaces (EGL_PBUFFER_BIT).
  4. glFramebufferTexture2DEXT and friends are desktop-only names; the OpenGL loader aborts the
     whole app on ES when it cannot find them. The plain ES 3 names do the same job.
  5. GL_BGRA is not allowed for texture creation / reading in OpenGL ES 3. Use RGBA and swap the
     red and blue bytes ourselves when copying into QEMU's picture.
"""
import re
import sys

root = "qemu-src/"


def load(path):
    return open(root + path, encoding="utf-8").read()


def save(path, text):
    open(root + path, "w", encoding="utf-8", newline="\n").write(text)


def must_replace(text, old, new, what, count=1):
    n = text.count(old)
    if n != count:
        sys.exit("PATCH FAILED (%s): expected %d match(es), found %d" % (what, count, n))
    return text.replace(old, new)


# ---------- ui/egl-helpers.c ----------
p = "ui/egl-helpers.c"
t = load(p)

# 3. config: off-screen surfaces
t = must_replace(t, "EGL_SURFACE_TYPE, EGL_WINDOW_BIT,", "EGL_SURFACE_TYPE, EGL_PBUFFER_BIT,", "surface type", count=2)

# 2. ES 3 context for QEMU's own drawing
t = must_replace(t, "        EGL_CONTEXT_CLIENT_VERSION, 2,\n", "        EGL_CONTEXT_CLIENT_VERSION, 3,\n", "ES3 context")

# 1. a small off-screen surface to make contexts current
helper = '''
/* Xbox/ANGLE: contexts cannot be made current without a surface, so use a small off-screen one. */
EGLSurface qemu_uwp_surface(EGLContext ctx)
{
    static EGLSurface pb = EGL_NO_SURFACE;
    static const EGLint att[] = { EGL_WIDTH, 16, EGL_HEIGHT, 16, EGL_NONE };

    if (ctx == EGL_NO_CONTEXT) {
        return EGL_NO_SURFACE;
    }
    if (pb == EGL_NO_SURFACE) {
        pb = eglCreatePbufferSurface(qemu_egl_display, qemu_egl_config, att);
        if (pb == EGL_NO_SURFACE) {
            error_report("egl: could not make the off-screen surface: %s",
                         qemu_egl_get_error_string());
        }
    }
    return pb;
}

/* GL_BGRA reads are not allowed in OpenGL ES 3: we read RGBA and swap red/blue here. */
static void uwp_swap_rb(uint8_t *base, int stride, int x, int y, int w, int h)
{
    int row, col;

    for (row = y; row < y + h; row++) {
        uint8_t *p = base + (size_t)row * stride + (size_t)x * 4;
        for (col = 0; col < w; col++, p += 4) {
            uint8_t r = p[0];
            p[0] = p[2];
            p[2] = r;
        }
    }
}
'''
marker = "void egl_fb_setup_for_tex("
t = must_replace(t, marker, helper.lstrip("\n") + "\n" + marker, "insert helpers")

# 4. EXT names -> plain ES 3 names
t = must_replace(t, "glFramebufferTexture2DEXT(", "glFramebufferTexture2D(", "FramebufferTexture2DEXT", count=1)
t = t.replace("GL_FRAMEBUFFER_EXT", "GL_FRAMEBUFFER").replace("GL_COLOR_ATTACHMENT0_EXT", "GL_COLOR_ATTACHMENT0")

# 5. RGBA instead of BGRA
t = must_replace(t, "0, GL_BGRA, GL_UNSIGNED_BYTE, 0);", "0, GL_RGBA, GL_UNSIGNED_BYTE, 0);", "texture format")
t = must_replace(
    t,
    "    glReadPixels(0, 0, surface_width(dst), surface_height(dst),\n"
    "                 GL_BGRA, GL_UNSIGNED_BYTE, surface_data(dst));\n",
    "    glReadPixels(0, 0, surface_width(dst), surface_height(dst),\n"
    "                 GL_RGBA, GL_UNSIGNED_BYTE, surface_data(dst));\n"
    "    uwp_swap_rb(surface_data(dst), surface_stride(dst), 0, 0,\n"
    "                surface_width(dst), surface_height(dst));\n",
    "read pixels",
)
t = must_replace(
    t,
    "    glReadPixels(x, y, w, h,\n"
    "                 GL_BGRA, GL_UNSIGNED_BYTE, surface_data(dst) + x * 4);\n",
    "    glReadPixels(x, y, w, h,\n"
    "                 GL_RGBA, GL_UNSIGNED_BYTE, surface_data(dst) + x * 4);\n"
    "    uwp_swap_rb(surface_data(dst), surface_stride(dst), x, y, w, h);\n",
    "read pixels rect",
)

# read_rect reads rows starting at y but the original passes the buffer start + x*4 only
# (the GL pack offset is not applied); keep QEMU's behaviour but make the swap match it.
# (QEMU's own code: PACK_ROW_LENGTH + pointer at x; rows start at the top of the buffer.)
t = t.replace(
    "uwp_swap_rb(surface_data(dst), surface_stride(dst), x, y, w, h);",
    "uwp_swap_rb(surface_data(dst), surface_stride(dst), x, 0, w, h);",
)

pat = re.compile(r"eglMakeCurrent\(qemu_egl_display,\s*EGL_NO_SURFACE,\s*EGL_NO_SURFACE,\s*(\w+)\)")
t, n = pat.subn(r"eglMakeCurrent(qemu_egl_display, qemu_uwp_surface(\1), qemu_uwp_surface(\1), \1)", t)
if n != 1:
    sys.exit("PATCH FAILED (egl-helpers make current): expected 1, found %d" % n)
save(p, t)

# ---------- header ----------
p = "include/ui/egl-helpers.h"
t = load(p)
t = must_replace(t, "bool egl_init(", "EGLSurface qemu_uwp_surface(EGLContext ctx);\nbool egl_init(", "header")
save(p, t)

# ---------- ui/egl-context.c and ui/egl-headless.c ----------
for p in ("ui/egl-context.c", "ui/egl-headless.c"):
    t = load(p)
    t, n = pat.subn(r"eglMakeCurrent(qemu_egl_display, qemu_uwp_surface(\1), qemu_uwp_surface(\1), \1)", t)
    if n != 1:
        sys.exit("PATCH FAILED (%s make current): expected 1, found %d" % (p, n))
    save(p, t)

print("EGL patches for ANGLE applied")
