/* Entry points that let the app drive QEMU once it is built as a DLL. */
#include <stdio.h>
#include <windows.h>
#include <stdlib.h>
#include <string.h>
#include <fcntl.h>

/* QEMU's main() renamed by the build (a function literally called main makes the
   compiler re-run all start-up code, which registers every QEMU part twice). */
extern int qemu_real_main(int argc, char **argv);

/* The build links with --wrap=exit, so every exit() inside QEMU lands in
   __wrap_exit below instead of closing the whole app. */
extern void __real_exit(int status);

extern void qemu_system_shutdown_request(int reason);

__declspec(dllexport) int qemu_dll_ping(void) { return 777; }

/* Asks a running QEMU to shut down cleanly (5 = "host-ui", the same as closing its window).
   Safe to call from a different thread than the one running qemu_dll_main. */
__declspec(dllexport) void qemu_dll_stop(void) { qemu_system_shutdown_request(5); }

static void *g_jmp[5];           /* where to come back to */
static volatile int g_status;    /* the code QEMU passed to exit() */
static DWORD g_main_thread;      /* only the thread that started QEMU may jump back */

void __wrap_exit(int status)
{
    fflush(stdout);
    fflush(stderr);
    if (g_main_thread != 0 && GetCurrentThreadId() == g_main_thread) {
        g_status = status;
        __builtin_longjmp(g_jmp, 1);
    }
    __real_exit(status);         /* some other thread: nothing safe to do */
}

/* Runs QEMU's normal main(). Text QEMU prints is saved to outfile.
   When QEMU calls exit(), control comes back here and the app stays open. */
__declspec(dllexport) int qemu_dll_main(const char *outfile, int argc, char **argv)
{
    if (outfile) {
        freopen(outfile, "w", stdout);
        freopen(outfile, "a", stderr);
        setvbuf(stdout, NULL, _IONBF, 0);
        setvbuf(stderr, NULL, _IONBF, 0);
    }
    /* QEMU opens files without asking for binary mode (it expects that to be the default).
       Without this, Windows turns every 0x0A byte into 0x0D 0x0A and stops reading at 0x1A,
       which corrupts saved states. */
    _set_fmode(_O_BINARY);
    /* Graphics test: when the Virgl graphics card is in use, ask virglrenderer to explain what it does
       and why it rejects something. (Only then, so normal use stays quiet.) */
    {
        int i;
        for (i = 0; i < argc; i++) {
            if (argv[i] && strstr(argv[i], "virtio-vga-gl")) {
                /* the renderer's own option names: obj = print each object it creates, feat = log the GPU features it found */
                _putenv("VREND_DEBUG=obj,feat");
                _putenv("VIRGL_DEBUG=obj,feat");
                break;
            }
        }
    }
    g_main_thread = GetCurrentThreadId();
    if (__builtin_setjmp(g_jmp) == 0) {
        int r = qemu_real_main(argc, argv);
        g_main_thread = 0;
        return r;
    }
    g_main_thread = 0;
    return g_status;
}

/* ---- text-command bridge (used for save / resume state) ----
   Runs one monitor command (like "stop" or "migrate -d file:...") inside QEMU's main loop
   and copies its text answer into out. Safe to call from any thread except QEMU's main one.
   Returns 0 when the command ran, -1 when it timed out. */
typedef struct HmpJob {
    char *cmd;
    char *answer;
    volatile LONG state;     /* 0 = waiting, 1 = one side is done; the second side frees */
    HANDLE done;
} HmpJob;

typedef struct AioContext AioContext;
extern AioContext *qemu_get_aio_context(void);
extern void aio_bh_schedule_oneshot_full(AioContext *ctx, void (*cb)(void *), void *opaque, const char *name);
extern char *qmp_human_monitor_command(const char *command_line, _Bool has_cpu_index, long long cpu_index, void **errp);
extern void g_free(void *p);

static void hmp_job_free(HmpJob *j)
{
    free(j->cmd);
    free(j->answer);
    CloseHandle(j->done);
    free(j);
}

static void hmp_bh(void *opaque)
{
    HmpJob *j = (HmpJob *)opaque;
    void *err = NULL;
    char *r = qmp_human_monitor_command(j->cmd, 0, 0, &err);
    if (r) {
        j->answer = _strdup(r);
        g_free(r);
    } else {
        j->answer = _strdup("(command failed)");
    }
    SetEvent(j->done);
    if (InterlockedExchange(&j->state, 1) == 1) hmp_job_free(j);
}

__declspec(dllexport) int qemu_dll_hmp(const char *cmd, char *out, int cap, int timeout_ms)
{
    HmpJob *j;
    int rc;
    if (!qemu_get_aio_context()) return -2;     /* QEMU's main loop does not exist yet */
    j = (HmpJob *)calloc(1, sizeof(HmpJob));
    if (!j) return -1;
    j->cmd = _strdup(cmd);
    j->done = CreateEventW(NULL, TRUE, FALSE, NULL);
    aio_bh_schedule_oneshot_full(qemu_get_aio_context(), hmp_bh, j, "uwp-hmp");
    if (WaitForSingleObject(j->done, (DWORD)timeout_ms) == WAIT_OBJECT_0) {
        if (out && cap > 0) {
            strncpy(out, j->answer ? j->answer : "", (size_t)cap - 1);
            out[cap - 1] = 0;
        }
        rc = 0;
    } else {
        if (out && cap > 0) out[0] = 0;
        rc = -1;
    }
    if (InterlockedExchange(&j->state, 1) == 1) hmp_job_free(j);
    return rc;
}
