/* A screen, keyboard and mouse for QEMU inside the Xbox app.
 *
 * QEMU normally shows the virtual PC's screen with its own window (SDL, GTK ...).
 * That is not possible in an Xbox app, so this file plugs in a tiny "display" that
 * only copies the virtual screen into a memory buffer. The app (C#) reads that buffer
 * about 30 times a second and draws it. Keys and mouse movement go the other way:
 * the app calls the functions at the bottom, which queue the events for QEMU.
 *
 * This file is compiled inside the QEMU build (same headers and flags as QEMU's own
 * ui/console.c), see native/compile_like_qemu.py.
 */
#include "qemu/osdep.h"
#include "qemu/thread.h"
#include "qemu/main-loop.h"
#include "ui/console.h"
#include "ui/input.h"
#include "ui/surface.h"

#define EXPORT __attribute__((dllexport))

#define MAX_W 1280
#define MAX_H 1024

/* ---------- screen ---------- */

static DisplayChangeListener g_dcl;
static DisplaySurface *g_surface;
static bool g_dirty;

static QemuMutex g_frame_lock;
static uint32_t *g_frame;       /* finished picture, tightly packed, BGRA, alpha forced to 255 */
static int g_frame_w, g_frame_h;
static int g_frame_seq;         /* goes up every time the picture changes */
static volatile int g_ready;    /* 1 once everything below is set up */

static void uwp_gfx_switch(DisplayChangeListener *dcl, DisplaySurface *new_surface)
{
    g_surface = new_surface;
    g_dirty = true;
}

static void uwp_gfx_update(DisplayChangeListener *dcl, int x, int y, int w, int h)
{
    g_dirty = true;
}

/* Only accept the 32-bit format we copy from; QEMU's VGA converts for us otherwise. */
static bool uwp_check_format(DisplayChangeListener *dcl, pixman_format_code_t format)
{
    return format == PIXMAN_x8r8g8b8;
}

static void uwp_refresh(DisplayChangeListener *dcl)
{
    DisplaySurface *s;
    int w, h, y, x, stride;
    const uint8_t *src;

    graphic_hw_update(dcl->con);

    s = g_surface;
    if (!g_dirty || !s) {
        return;
    }
    g_dirty = false;

    if (surface_bytes_per_pixel(s) != 4) {
        return;
    }
    w = surface_width(s);
    h = surface_height(s);
    if (w > MAX_W) {
        w = MAX_W;
    }
    if (h > MAX_H) {
        h = MAX_H;
    }
    stride = surface_stride(s);
    src = surface_data(s);

    qemu_mutex_lock(&g_frame_lock);
    for (y = 0; y < h; y++) {
        const uint32_t *row = (const uint32_t *)(src + (size_t)y * stride);
        uint32_t *out = g_frame + (size_t)y * w;
        for (x = 0; x < w; x++) {
            out[x] = row[x] | 0xFF000000u;
        }
    }
    g_frame_w = w;
    g_frame_h = h;
    g_frame_seq++;
    qemu_mutex_unlock(&g_frame_lock);
}

static const DisplayChangeListenerOps g_dcl_ops = {
    .dpy_name          = "xbox-uwp",
    .dpy_refresh       = uwp_refresh,
    .dpy_gfx_update    = uwp_gfx_update,
    .dpy_gfx_switch    = uwp_gfx_switch,
    .dpy_gfx_check_format = uwp_check_format,
};

/* ---------- keyboard and mouse ---------- */

enum { EV_KEY = 1, EV_MOUSE = 2, EV_ABS = 3 };

typedef struct UwpEvent {
    int type;
    int a, b, c, d;   /* key: a = key number, b = down.  mouse: a = dx, b = dy, c = wheel, d = buttons */
} UwpEvent;

#define QUEUE_SIZE 2048
static UwpEvent g_queue[QUEUE_SIZE];
static int g_q_head, g_q_tail;
static QemuMutex g_q_lock;
static QEMUBH *g_input_bh;
static int g_buttons_down;      /* buttons QEMU has been told are pressed (1 left, 2 right, 4 middle) */

static void queue_push(const UwpEvent *ev)
{
    int next;
    if (!g_ready) {
        return;
    }
    qemu_mutex_lock(&g_q_lock);
    next = (g_q_tail + 1) % QUEUE_SIZE;
    if (next != g_q_head) {         /* drop the event if the queue is full */
        g_queue[g_q_tail] = *ev;
        g_q_tail = next;
    }
    qemu_mutex_unlock(&g_q_lock);
    qemu_bh_schedule(g_input_bh);
}

static void set_button(int mask, InputButton btn, int want)
{
    bool now = (g_buttons_down & mask) != 0;
    if (want && !now) {
        qemu_input_queue_btn(NULL, btn, true);
        g_buttons_down |= mask;
    } else if (!want && now) {
        qemu_input_queue_btn(NULL, btn, false);
        g_buttons_down &= ~mask;
    }
}

/* Runs inside QEMU's own thread, so it is safe to talk to the input system here. */
static void input_bh(void *opaque)
{
    for (;;) {
        UwpEvent ev;

        qemu_mutex_lock(&g_q_lock);
        if (g_q_head == g_q_tail) {
            qemu_mutex_unlock(&g_q_lock);
            break;
        }
        ev = g_queue[g_q_head];
        g_q_head = (g_q_head + 1) % QUEUE_SIZE;
        qemu_mutex_unlock(&g_q_lock);

        if (ev.type == EV_KEY) {
            qemu_input_event_send_key_number(NULL, ev.a, ev.b != 0);
            qemu_input_event_sync();
        } else if (ev.type == EV_ABS) {
            /* a = x, b = y, both 0..32767 across the whole virtual screen */
            qemu_input_queue_abs(NULL, INPUT_AXIS_X, ev.a, 0, 32767);
            qemu_input_queue_abs(NULL, INPUT_AXIS_Y, ev.b, 0, 32767);
            set_button(1, INPUT_BUTTON_LEFT, ev.d & 1);
            set_button(2, INPUT_BUTTON_RIGHT, ev.d & 2);
            set_button(4, INPUT_BUTTON_MIDDLE, ev.d & 4);
            if (ev.c > 0) {
                qemu_input_queue_btn(NULL, INPUT_BUTTON_WHEEL_UP, true);
                qemu_input_queue_btn(NULL, INPUT_BUTTON_WHEEL_UP, false);
            } else if (ev.c < 0) {
                qemu_input_queue_btn(NULL, INPUT_BUTTON_WHEEL_DOWN, true);
                qemu_input_queue_btn(NULL, INPUT_BUTTON_WHEEL_DOWN, false);
            }
            qemu_input_event_sync();
        } else if (ev.type == EV_MOUSE) {
            if (ev.a) {
                qemu_input_queue_rel(NULL, INPUT_AXIS_X, ev.a);
            }
            if (ev.b) {
                qemu_input_queue_rel(NULL, INPUT_AXIS_Y, ev.b);
            }
            set_button(1, INPUT_BUTTON_LEFT, ev.d & 1);
            set_button(2, INPUT_BUTTON_RIGHT, ev.d & 2);
            set_button(4, INPUT_BUTTON_MIDDLE, ev.d & 4);
            if (ev.c > 0) {
                qemu_input_queue_btn(NULL, INPUT_BUTTON_WHEEL_UP, true);
                qemu_input_queue_btn(NULL, INPUT_BUTTON_WHEEL_UP, false);
            } else if (ev.c < 0) {
                qemu_input_queue_btn(NULL, INPUT_BUTTON_WHEEL_DOWN, true);
                qemu_input_queue_btn(NULL, INPUT_BUTTON_WHEEL_DOWN, false);
            }
            qemu_input_event_sync();
        }
    }
}

/* ---------- start-up hook (called from QEMU's main loop start, see build.yml) ---------- */

void uwp_after_init(void)
{
    QemuConsole *con;

    qemu_mutex_init(&g_frame_lock);
    qemu_mutex_init(&g_q_lock);
    g_frame = g_malloc0((size_t)MAX_W * MAX_H * 4);
    g_q_head = g_q_tail = 0;
    g_buttons_down = 0;
    g_frame_seq = 0;
    g_input_bh = qemu_bh_new(input_bh, NULL);

    con = qemu_console_lookup_by_index(0);
    if (con && qemu_console_is_graphic(con)) {
        g_dcl.con = con;
        g_dcl.ops = &g_dcl_ops;
        register_displaychangelistener(&g_dcl);
        fprintf(stderr, "uwp_display: screen attached\n");
    } else {
        fprintf(stderr, "uwp_display: this virtual PC has no screen (no VGA device)\n");
    }
    g_ready = 1;
}

/* ---------- functions the app calls ---------- */

/* Copies the newest picture into dst (BGRA, w*h*4 bytes) if it changed since *seq.
   Returns 1 when dst now holds a new picture, 0 when nothing changed. */
EXPORT int qemu_dll_get_frame(uint8_t *dst, int cap_bytes, int *w, int *h, int *seq)
{
    int result = 0;

    if (!g_ready || !dst || !w || !h || !seq) {
        return 0;
    }
    qemu_mutex_lock(&g_frame_lock);
    if (g_frame_seq != *seq && g_frame_w > 0 && g_frame_h > 0 &&
        (size_t)g_frame_w * g_frame_h * 4 <= (size_t)cap_bytes) {
        memcpy(dst, g_frame, (size_t)g_frame_w * g_frame_h * 4);
        *w = g_frame_w;
        *h = g_frame_h;
        *seq = g_frame_seq;
        result = 1;
    }
    qemu_mutex_unlock(&g_frame_lock);
    return result;
}

/* key_number: PC scan code (set 1); keys with an 0xE0 prefix have 0x80 added.  down: 1 pressed, 0 released. */
EXPORT void qemu_dll_key(int key_number, int down)
{
    UwpEvent ev = { EV_KEY, key_number, down, 0, 0 };
    queue_push(&ev);
}

/* dx, dy: how far the mouse moved.  wheel: >0 up, <0 down.  buttons: 1 left, 2 right, 4 middle (held now). */
EXPORT void qemu_dll_mouse(int dx, int dy, int wheel, int buttons)
{
    UwpEvent ev = { EV_MOUSE, dx, dy, wheel, buttons };
    queue_push(&ev);
}

/* x, y: where the pointer is, 0..32767 across the whole virtual screen (for the virtio-tablet device).
   wheel: >0 up, <0 down.  buttons: 1 left, 2 right, 4 middle (held now). */
EXPORT void qemu_dll_abs(int x, int y, int wheel, int buttons)
{
    UwpEvent ev = { EV_ABS, x, y, wheel, buttons };
    queue_push(&ev);
}
