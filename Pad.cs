using System;
using Windows.Gaming.Input;

namespace XboxVmHost
{
    public sealed partial class MainPage
    {
        private GamepadButtons _padPrev = GamepadButtons.None;

        // Reads the controller every VM tick, so D-pad and A work even in mouse mode (where the system does not send them as keys).
        private void PollGamepad()
        {
            Gamepad pad = null;
            try { if (Gamepad.Gamepads.Count > 0) pad = Gamepad.Gamepads[0]; } catch { }
            if (pad == null) { _padPrev = GamepadButtons.None; return; }

            GamepadButtons now = pad.GetCurrentReading().Buttons;
            GamepadButtons pressed = now & ~_padPrev;
            GamepadButtons released = _padPrev & ~now;
            _padPrev = now;
            if (pressed == GamepadButtons.None && released == GamepadButtons.None) return;

            if (_oskOpen)
            {
                if ((pressed & GamepadButtons.DPadUp) != 0) OskMove(-1, 0);
                if ((pressed & GamepadButtons.DPadDown) != 0) OskMove(1, 0);
                if ((pressed & GamepadButtons.DPadLeft) != 0) OskMove(0, -1);
                if ((pressed & GamepadButtons.DPadRight) != 0) OskMove(0, 1);
                if ((pressed & GamepadButtons.A) != 0) OskPress(true);
                if ((released & GamepadButtons.A) != 0) OskPress(false);
                return;
            }

            PadKey(pressed, released, GamepadButtons.DPadUp, 0xC8);
            PadKey(pressed, released, GamepadButtons.DPadDown, 0xD0);
            PadKey(pressed, released, GamepadButtons.DPadLeft, 0xCB);
            PadKey(pressed, released, GamepadButtons.DPadRight, 0xCD);
            PadKey(pressed, released, GamepadButtons.A, 0x1C);
        }

        private void PadKey(GamepadButtons pressed, GamepadButtons released, GamepadButtons btn, int code)
        {
            if ((pressed & btn) != 0) qemu_dll_key(code, 1);
            if ((released & btn) != 0) qemu_dll_key(code, 0);
        }
    }
}
