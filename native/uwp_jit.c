/* Memory for QEMU's code generator (the JIT buffer), done the way UWP/Xbox apps are allowed to.
   QEMU normally calls VirtualAlloc(... PAGE_EXECUTE_READWRITE), which an Xbox app may not do.
   Here we ask for read/write memory first, then switch it to read+write+execute.
   No windows.h on purpose (it would declare the blocked versions). */
#include <stdio.h>
#include <stddef.h>

__declspec(dllimport) void *__stdcall VirtualAllocFromApp(void *, size_t, unsigned long, unsigned long);
__declspec(dllimport) int __stdcall VirtualProtectFromApp(void *, size_t, unsigned long, unsigned long *);
__declspec(dllimport) unsigned long __stdcall GetLastError(void);
__declspec(dllimport) void __stdcall SetLastError(unsigned long);

#define MEM_COMMIT_RESERVE 0x3000
#define PAGE_READWRITE 0x04
#define PAGE_EXECUTE_READWRITE 0x40

void *uwp_jit_alloc(size_t size)
{
    unsigned long old = 0, err;
    void *p = VirtualAllocFromApp(NULL, size, MEM_COMMIT_RESERVE, PAGE_READWRITE);
    if (!p) {
        err = GetLastError();
        fprintf(stderr, "uwp_jit: VirtualAllocFromApp(%zu bytes) failed, error %lu\n", size, err);
        SetLastError(err);
        return NULL;
    }
    if (!VirtualProtectFromApp(p, size, PAGE_EXECUTE_READWRITE, &old)) {
        err = GetLastError();
        fprintf(stderr, "uwp_jit: making the %zu byte code buffer read+write+execute failed, error %lu\n", size, err);
        SetLastError(err);
        return NULL;
    }
    fprintf(stderr, "uwp_jit: code buffer of %zu bytes is ready (read+write+execute)\n", size);
    return p;
}
