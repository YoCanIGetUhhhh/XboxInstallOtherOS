using System;
using System.Collections.Generic;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace XboxVmHost
{
    public sealed partial class MainPage
    {
        private bool _oskOpen;
        private Grid _oskPanel;
        private Border[][] _oskCells;
        private int[][] _oskCodes;
        private int _oskR, _oskC;
        private readonly HashSet<int> _oskHeld = new HashSet<int>();

        private static readonly string[] OskSpec = {
            "Esc|01 1|02 2|03 3|04 4|05 5|06 6|07 7|08 8|09 9|0A 0|0B -|0C =|0D Bksp|0E",
            "Tab|0F q|10 w|11 e|12 r|13 t|14 y|15 u|16 i|17 o|18 p|19 [|1A ]|1B",
            "Ctrl|1D a|1E s|1F d|20 f|21 g|22 h|23 j|24 k|25 l|26 ;|27 '|28 Enter|1C",
            "Shift|2A z|2C x|2D c|2E v|2F b|30 n|31 m|32 ,|33 .|34 /|35",
            "Alt|38 `|29 \\|2B Space|39 \u2190|CB \u2193|D0 \u2191|C8 \u2192|CD"
        };

        private static bool OskSticky(int c) { return c == 0x1D || c == 0x2A || c == 0x38; }

        private void OskBuild()
        {
            var stack = new StackPanel { Margin = new Thickness(8) };
            _oskCells = new Border[OskSpec.Length][];
            _oskCodes = new int[OskSpec.Length][];
            for (int r = 0; r < OskSpec.Length; r++)
            {
                string[] t = OskSpec[r].Split(' ');
                _oskCells[r] = new Border[t.Length];
                _oskCodes[r] = new int[t.Length];
                var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                for (int c = 0; c < t.Length; c++)
                {
                    int bar = t[c].LastIndexOf('|');
                    string label = t[c].Substring(0, bar);
                    _oskCodes[r][c] = Convert.ToInt32(t[c].Substring(bar + 1), 16);
                    double w = label == "Space" ? 200 : (label.Length > 1 && char.IsLetter(label[0]) ? 70 : 52);
                    var cell = new Border { Width = w, Height = 44, Margin = new Thickness(2) };
                    cell.Child = new TextBlock { Text = label, FontSize = 18, Foreground = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    _oskCells[r][c] = cell;
                    row.Children.Add(cell);
                }
                stack.Children.Add(row);
            }
            _oskPanel = new Grid { Background = new SolidColorBrush(Color.FromArgb(230, 16, 16, 16)), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
            _oskPanel.Children.Add(stack);
            Grid.SetRowSpan(_oskPanel, 99);
            Grid.SetColumnSpan(_oskPanel, 99);
            VmGrid.Children.Add(_oskPanel);
        }

        private void OskPaint()
        {
            for (int r = 0; r < _oskCells.Length; r++)
                for (int c = 0; c < _oskCells[r].Length; c++)
                {
                    bool sel = r == _oskR && c == _oskC;
                    bool held = _oskHeld.Contains(_oskCodes[r][c]);
                    _oskCells[r][c].Background = new SolidColorBrush(sel ? Color.FromArgb(255, 16, 124, 16) : held ? Color.FromArgb(255, 0, 99, 177) : Color.FromArgb(255, 43, 43, 43));
                }
        }

        private void OskReleaseMods()
        {
            foreach (int c in _oskHeld) qemu_dll_key(c, 0);
            _oskHeld.Clear();
        }

        private void OskSetOpen(bool open)
        {
            if (_oskPanel == null) OskBuild();
            _oskOpen = open;
            _oskPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (!open) OskReleaseMods();
            OskPaint();
        }

        private void OskMove(int dr, int dc)
        {
            int r = Math.Max(0, Math.Min(_oskCells.Length - 1, _oskR + dr));
            int c = dr != 0 ? _oskC : _oskC + dc;
            c = Math.Max(0, Math.Min(_oskCells[r].Length - 1, c));
            _oskR = r; _oskC = c;
            OskPaint();
        }

        private void OskPress(bool down)
        {
            int code = _oskCodes[_oskR][_oskC];
            if (OskSticky(code))
            {
                if (!down) return;
                if (_oskHeld.Remove(code)) qemu_dll_key(code, 0);
                else { _oskHeld.Add(code); qemu_dll_key(code, 1); }
            }
            else if (down) qemu_dll_key(code, 1);
            else { qemu_dll_key(code, 0); OskReleaseMods(); }
            OskPaint();
        }

        // Returns true when the key was used by the on-screen keyboard (so it must not go to the virtual PC).
        private bool OskHandleKey(AcceleratorKeyEventArgs e, bool down)
        {
            VirtualKey k = e.VirtualKey;
            bool first = !e.KeyStatus.WasKeyDown;
            if (!_oskOpen)
            {
                if (k == VirtualKey.GamepadY) { if (down && first) OskSetOpen(true); return true; }
                return false;
            }
            if (k == VirtualKey.GamepadY || k == VirtualKey.GamepadB) { if (down && first) OskSetOpen(false); return true; }
            if (k == VirtualKey.GamepadA) { if (down) { if (first) OskPress(true); } else OskPress(false); return true; }
            if (down)
            {
                if (k == VirtualKey.GamepadDPadUp) { OskMove(-1, 0); return true; }
                if (k == VirtualKey.GamepadDPadDown) { OskMove(1, 0); return true; }
                if (k == VirtualKey.GamepadDPadLeft) { OskMove(0, -1); return true; }
                if (k == VirtualKey.GamepadDPadRight) { OskMove(0, 1); return true; }
            }
            int kv = (int)k;
            return kv >= 195 && kv <= 224;   // swallow every other gamepad button while the keyboard is open
        }
    }
}
