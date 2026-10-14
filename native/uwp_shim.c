/* Stand-ins for Windows functions that UWP (Xbox) apps are not allowed to import.
   They are defined here with their real names, so the linker uses these instead of
   the Windows import library, and the finished DLL never lists the forbidden names.
   No windows.h on purpose: that header would declare them as imports. */
typedef void *PV;
typedef int BOOL_;
typedef unsigned long DW;
typedef unsigned long long SZ;
typedef long HR;

/* allowed functions we build on (checked against WindowsApp.lib at build time) */
__declspec(dllimport) PV  __stdcall CreateFiberEx(SZ, SZ, DW, PV, PV);
__declspec(dllimport) PV  __stdcall ConvertThreadToFiberEx(PV, DW);
__declspec(dllimport) DW  __stdcall WaitForMultipleObjectsEx(DW, const PV *, BOOL_, DW, BOOL_);
__declspec(dllimport) void __stdcall SetLastError(DW);
__declspec(dllimport) PV  __stdcall LoadPackagedLibrary(const unsigned short *, DW);
__declspec(dllimport) int __stdcall MultiByteToWideChar(unsigned, DW, const char *, int, unsigned short *, int);

#define ERR_NOT_SUPPORTED 50UL

PV __stdcall CreateFiber(SZ stack, PV fn, PV param) { return CreateFiberEx(stack, stack, 0, fn, param); }
PV __stdcall ConvertThreadToFiber(PV param) { return ConvertThreadToFiberEx(param, 0); }

BOOL_ __stdcall CommConfigDialogA(const char *a, PV b, PV c) { (void)a; (void)b; (void)c; SetLastError(ERR_NOT_SUPPORTED); return 0; }
BOOL_ __stdcall GetDefaultCommConfigA(const char *a, PV b, DW *c) { (void)a; (void)b; (void)c; SetLastError(ERR_NOT_SUPPORTED); return 0; }
HR    __stdcall CoInitialize(PV r) { (void)r; return 0; }
PV    __stdcall GetDesktopWindow(void) { return 0; }
BOOL_ __stdcall GetUserNameW(unsigned short *b, DW *n) { (void)b; (void)n; SetLastError(ERR_NOT_SUPPORTED); return 0; }
HR    __stdcall SHGetKnownFolderPath(const PV id, DW f, PV tok, unsigned short **out) { (void)id; (void)f; (void)tok; if (out) *out = 0; return (HR)0x80004001; }
BOOL_ __stdcall IsWindow(PV h) { (void)h; return 0; }
int   __stdcall MessageBoxW(PV h, const unsigned short *t, const unsigned short *c, unsigned u) { (void)h; (void)t; (void)c; (void)u; return 0; }
DW    __stdcall MsgWaitForMultipleObjectsEx(DW n, const PV *h, DW ms, DW mask, DW flags)
{
    (void)mask;
    return WaitForMultipleObjectsEx(n, h, (flags & 1) ? 1 : 0, ms, (flags & 2) ? 1 : 0);
}
BOOL_ __stdcall PeekMessageW(PV m, PV h, unsigned a, unsigned b, unsigned r) { (void)m; (void)h; (void)a; (void)b; (void)r; return 0; }
BOOL_ __stdcall PostMessageW(PV h, unsigned m, unsigned long long w, long long l) { (void)h; (void)m; (void)w; (void)l; return 0; }
BOOL_ __stdcall EnumResourceLanguagesA(PV m, const char *t, const char *n, PV f, long long p) { (void)m; (void)t; (void)n; (void)f; (void)p; return 0; }

/* libepoxy (used by the graphics code) opens libEGL.dll with LoadLibraryA. In an app only
   LoadPackagedLibrary is allowed, and it finds DLLs inside the app package. */
PV __stdcall LoadLibraryW(const unsigned short *name) { return LoadPackagedLibrary(name, 0); }
PV __stdcall LoadLibraryA(const char *name)
{
    unsigned short w[260];
    if (!name || MultiByteToWideChar(65001, 0, name, -1, w, 260) <= 0) { SetLastError(87); return 0; }
    return LoadPackagedLibrary(w, 0);
}

/* Structured-exception dispatcher: QEMU has no __try/__except blocks, so "keep looking" is correct. */
int __C_specific_handler(PV rec, PV frame, PV ctx, PV disp) { (void)rec; (void)frame; (void)ctx; (void)disp; return 1; }

/* The import-pointer names the compiler uses when calling the functions above. */
#define IMP(n) PV __imp_##n = (PV)n
IMP(CreateFiber); IMP(ConvertThreadToFiber); IMP(CommConfigDialogA); IMP(GetDefaultCommConfigA);
IMP(CoInitialize); IMP(GetDesktopWindow); IMP(GetUserNameW); IMP(SHGetKnownFolderPath);
IMP(IsWindow); IMP(MessageBoxW); IMP(MsgWaitForMultipleObjectsEx); IMP(PeekMessageW);
IMP(LoadLibraryA); IMP(LoadLibraryW); IMP(PostMessageW); IMP(EnumResourceLanguagesA); IMP(__C_specific_handler);
