using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace XboxVmHost
{
    // Graphics test, stage 2: can ANGLE (OpenGL ES drawn through Direct3D 11) start inside this app on the Xbox?
    // It only asks ANGLE to start, make an off-screen drawing surface, clear it and read one pixel back.
    // Nothing here is used by the virtual PC yet.
    internal static class AngleTest
    {
        private const string EGL = "libEGL.dll";
        private const string GLES = "libGLESv2.dll";

        [DllImport(EGL)] private static extern IntPtr eglGetPlatformDisplayEXT(int platform, IntPtr nativeDisplay, int[] attribs);
        [DllImport(EGL)] private static extern IntPtr eglGetDisplay(IntPtr nativeDisplay);
        [DllImport(EGL)] private static extern int eglInitialize(IntPtr dpy, out int major, out int minor);
        [DllImport(EGL)] private static extern IntPtr eglQueryString(IntPtr dpy, int name);
        [DllImport(EGL)] private static extern int eglBindAPI(int api);
        [DllImport(EGL)] private static extern int eglChooseConfig(IntPtr dpy, int[] attribs, IntPtr[] configs, int size, out int num);
        [DllImport(EGL)] private static extern IntPtr eglCreatePbufferSurface(IntPtr dpy, IntPtr config, int[] attribs);
        [DllImport(EGL)] private static extern IntPtr eglCreateContext(IntPtr dpy, IntPtr config, IntPtr share, int[] attribs);
        [DllImport(EGL)] private static extern int eglMakeCurrent(IntPtr dpy, IntPtr draw, IntPtr read, IntPtr ctx);
        [DllImport(EGL)] private static extern int eglGetError();
        [DllImport(EGL)] private static extern int eglDestroyContext(IntPtr dpy, IntPtr ctx);
        [DllImport(EGL)] private static extern int eglDestroySurface(IntPtr dpy, IntPtr surface);
        [DllImport(EGL)] private static extern int eglTerminate(IntPtr dpy);

        [DllImport(GLES)] private static extern IntPtr glGetString(int name);
        [DllImport(GLES)] private static extern void glClearColor(float r, float g, float b, float a);
        [DllImport(GLES)] private static extern void glClear(int mask);
        [DllImport(GLES)] private static extern void glFinish();
        [DllImport(GLES)] private static extern void glReadPixels(int x, int y, int w, int h, int format, int type, byte[] data);
        [DllImport(GLES)] private static extern int glGetError();

        private const int EGL_NONE = 0x3038;
        private const int EGL_VENDOR = 0x3053;
        private const int EGL_VERSION = 0x3054;
        private const int EGL_OPENGL_ES_API = 0x30A0;

        private static string Hex(int v) { return "0x" + v.ToString("X"); }
        private static string Str(IntPtr p) { return p == IntPtr.Zero ? "(none)" : Marshal.PtrToStringAnsi(p); }

        // Returns the whole report as text. Never throws.
        public static string Run()
        {
            var sb = new StringBuilder();
            try { Steps(sb); }
            catch (DllNotFoundException ex) { sb.AppendLine("RESULT: FAILED - a graphics DLL could not be loaded (" + ex.Message + "). Is libEGL.dll / libGLESv2.dll inside the app package?"); }
            catch (EntryPointNotFoundException ex) { sb.AppendLine("RESULT: FAILED - a function is missing from the graphics DLL (" + ex.Message + ")."); }
            catch (BadImageFormatException ex) { sb.AppendLine("RESULT: FAILED - the graphics DLL is the wrong kind (" + ex.Message + ")."); }
            catch (Exception ex) { sb.AppendLine("RESULT: FAILED - " + ex.GetType().Name + ": " + ex.Message); }
            return sb.ToString().TrimEnd();
        }

        private static void Steps(StringBuilder sb)
        {
            var clock = Stopwatch.StartNew();

            sb.AppendLine("Step 1: loading libEGL.dll ...");
            int firstError = eglGetError();
            sb.AppendLine("  loaded (first EGL error code " + Hex(firstError) + ", 0x3000 means none)");

            sb.AppendLine("Step 2: asking ANGLE for a Direct3D 11 display ...");
            IntPtr dpy = IntPtr.Zero;
            try
            {
                // EGL_PLATFORM_ANGLE_ANGLE, type = Direct3D 11
                dpy = eglGetPlatformDisplayEXT(0x3202, IntPtr.Zero, new int[] { 0x3203, 0x3208, EGL_NONE });
                sb.AppendLine("  eglGetPlatformDisplayEXT: display = " + (dpy == IntPtr.Zero ? "none (error " + Hex(eglGetError()) + ")" : "ok"));
            }
            catch (EntryPointNotFoundException) { sb.AppendLine("  eglGetPlatformDisplayEXT not found, trying the default display"); }
            if (dpy == IntPtr.Zero) dpy = eglGetDisplay(IntPtr.Zero);
            if (dpy == IntPtr.Zero) { sb.AppendLine("RESULT: FAILED - no display (EGL error " + Hex(eglGetError()) + ")"); return; }

            sb.AppendLine("Step 3: starting ANGLE ...");
            int major, minor;
            if (eglInitialize(dpy, out major, out minor) == 0) { sb.AppendLine("RESULT: FAILED - eglInitialize failed (EGL error " + Hex(eglGetError()) + ")"); return; }
            sb.AppendLine("  EGL " + major + "." + minor + ", vendor " + Str(eglQueryString(dpy, EGL_VENDOR)) + ", version " + Str(eglQueryString(dpy, EGL_VERSION)));

            eglBindAPI(EGL_OPENGL_ES_API);

            sb.AppendLine("Step 4: choosing a drawing format and making an off-screen surface ...");
            int version = 3;
            IntPtr[] configs = new IntPtr[1];
            int num;
            int[] want3 = { 0x3040, 0x40, 0x3033, 0x1, 0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, EGL_NONE };
            if (eglChooseConfig(dpy, want3, configs, 1, out num) == 0 || num == 0)
            {
                sb.AppendLine("  no OpenGL ES 3 format, trying OpenGL ES 2");
                version = 2;
                int[] want2 = { 0x3040, 0x4, 0x3033, 0x1, 0x3024, 8, 0x3023, 8, 0x3022, 8, 0x3021, 8, EGL_NONE };
                if (eglChooseConfig(dpy, want2, configs, 1, out num) == 0 || num == 0) { sb.AppendLine("RESULT: FAILED - no usable drawing format (EGL error " + Hex(eglGetError()) + ")"); eglTerminate(dpy); return; }
            }
            IntPtr surface = eglCreatePbufferSurface(dpy, configs[0], new int[] { 0x3057, 256, 0x3056, 256, EGL_NONE });
            if (surface == IntPtr.Zero) { sb.AppendLine("RESULT: FAILED - could not make an off-screen surface (EGL error " + Hex(eglGetError()) + ")"); eglTerminate(dpy); return; }

            sb.AppendLine("Step 5: making an OpenGL ES " + version + " context ...");
            IntPtr ctx = eglCreateContext(dpy, configs[0], IntPtr.Zero, new int[] { 0x3098, version, EGL_NONE });
            if (ctx == IntPtr.Zero) { sb.AppendLine("RESULT: FAILED - could not make a context (EGL error " + Hex(eglGetError()) + ")"); eglDestroySurface(dpy, surface); eglTerminate(dpy); return; }
            if (eglMakeCurrent(dpy, surface, surface, ctx) == 0) { sb.AppendLine("RESULT: FAILED - eglMakeCurrent failed (EGL error " + Hex(eglGetError()) + ")"); eglDestroyContext(dpy, ctx); eglDestroySurface(dpy, surface); eglTerminate(dpy); return; }

            string vendor = Str(glGetString(0x1F00));
            string renderer = Str(glGetString(0x1F01));
            string glver = Str(glGetString(0x1F02));
            sb.AppendLine("  GL vendor:   " + vendor);
            sb.AppendLine("  GL renderer: " + renderer);
            sb.AppendLine("  GL version:  " + glver);

            sb.AppendLine("Step 6: clearing the surface to green and reading a pixel back ...");
            glClearColor(0f, 1f, 0f, 1f);
            glClear(0x4000);
            glFinish();
            byte[] px = new byte[4];
            glReadPixels(10, 10, 1, 1, 0x1908, 0x1401, px);
            int glErr = glGetError();
            sb.AppendLine("  pixel red/green/blue/alpha = " + px[0] + "/" + px[1] + "/" + px[2] + "/" + px[3] + " (GL error " + Hex(glErr) + ", 0x0 means none)");
            bool pixelOk = px[0] == 0 && px[1] == 255 && px[2] == 0 && px[3] == 255;

            eglMakeCurrent(dpy, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            eglDestroyContext(dpy, ctx);
            eglDestroySurface(dpy, surface);
            eglTerminate(dpy);

            sb.AppendLine(pixelOk
                ? "RESULT: OK - ANGLE started and drew correctly (" + clock.ElapsedMilliseconds + " ms). Renderer: " + renderer
                : "RESULT: PARTLY - ANGLE started but the pixel was not the expected green. Renderer: " + renderer);
        }
    }
}
