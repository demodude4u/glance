using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using static Glance.Win32;

namespace Glance;

/// <summary>
/// Pictures of the model: the Picture card (copy, save, size, background), rendering at several times the
/// window's size, and writing PNG files. A picture never includes the toolbar, readouts or cards.
/// </summary>
internal static unsafe partial class App
{
    static bool _picture;                   // the Picture card is open
    static bool _shotAlpha;                 // the frame being rendered has no backdrop
    static float _shotK = 1;                // the frame being rendered is this many times the window's size
    static int _hotPic = -1;
    static (float X, float Y, float W, float H) _picButton;
    static readonly List<(float X, float Y, float W, float H, int Action)> _picButtons = new();

    static void TogglePicture()
    {
        if (_mesh == null) return;
        _picture = !_picture;
        if (_picture) { _fixUnits = false; _help = false; }
    }

    /// <summary>The picture's size in pixels: the window times the chosen factor, within what the graphics card can do.</summary>
    static (int W, int H, int K) PictureSize()
    {
        int k = Math.Clamp(Settings.PictureScale, 1, 4), max = Math.Min(R.MaxSize, 8192);
        while (k > 1 && (_w * k > max || _h * k > max)) k--;
        return (_w * k, _h * k, k);
    }

    /// <summary>Renders the current view without any overlay. BGRA, bottom row first; premultiplied when transparent.</summary>
    static byte[] RenderPicture(bool transparent, out int w, out int h)
    {
        var (pw, ph, k) = PictureSize();
        int ow = _w, oh = _h;
        _w = pw; _h = ph; _shotAlpha = transparent; _shotK = k;
        try
        {
            Render(hud: false, toWindow: false);
            w = pw; h = ph;
            return R.ReadResolved(pw, ph);
        }
        finally { _w = ow; _h = oh; _shotAlpha = false; _shotK = 1; Invalidate(); }
    }

    static void CopyImage()
    {
        if (_mesh == null) return;
        byte[] px = RenderPicture(false, out int w, out int h);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        nint mem = GlobalAlloc(0x0002, (nuint)(40 + px.Length));
        if (mem == 0) return;
        byte* dst = (byte*)GlobalLock(mem);
        *(BITMAPINFOHEADER*)dst = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = h, biPlanes = 1, biBitCount = 32, biSizeImage = (uint)px.Length };
        Marshal.Copy(px, 0, (nint)(dst + 40), px.Length);
        GlobalUnlock(mem);
        if (OpenClipboard(_hwnd) != 0)
        {
            EmptyClipboard(); SetClipboardData(8, mem); CloseClipboard();
            Toast($"Picture copied  ·  {w:N0} × {h:N0}");
        }
    }

    /// <summary>Asks where to save (unless told), renders, and writes the PNG in the background.</summary>
    static void SavePicture(string? path = null, bool wait = false)
    {
        if (_mesh == null) return;
        if (path == null)
        {
            char* buf = stackalloc char[2048];
            string name = Path.GetFileNameWithoutExtension(_path ?? "model") + ".png";
            name.AsSpan().CopyTo(new Span<char>(buf, 2047)); buf[Math.Min(name.Length, 2047)] = '\0';
            fixed (char* filter = "PNG picture\0*.png\0\0") fixed (char* ext = "png") fixed (char* title = "Save picture")
            {
                var ofn = new OPENFILENAMEW
                {
                    lStructSize = (uint)sizeof(OPENFILENAMEW), hwndOwner = _hwnd, lpstrFilter = filter, lpstrFile = buf, nMaxFile = 2048,
                    lpstrDefExt = ext, lpstrTitle = title, Flags = 0x00080000 | 0x800 | 0x8 | 0x2,      // explorer, path must exist, keep folder, confirm overwrite
                };
                bool ok = GetSaveFileNameW(&ofn) != 0;
                _drag = 0; Invalidate();
                if (!ok) return;
            }
            path = new string(buf);
        }
        bool transparent = Settings.PictureAlpha;
        byte[] px = RenderPicture(transparent, out int w, out int h);
        string file = path;
        void Write()
        {
            try
            {
                WritePng(file, px, w, h, transparent);
                Post(() => Toast($"Saved {Path.GetFileName(file)}  ·  {w:N0} × {h:N0}", 3f));
            }
            catch (Exception ex) { Post(() => Toast("Couldn't save the picture: " + ex.Message, 5f)); }
        }
        if (wait) Write();
        else { Toast("Saving…"); Task.Run(Write); }
    }

    // ───────────────────────── PNG ─────────────────────────

    static readonly uint[] CrcTable = MakeCrcTable();
    static uint[] MakeCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static void Chunk(Stream f, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(head, (uint)data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        uint crc = 0xFFFFFFFF;
        for (int i = 4; i < 8; i++) crc = CrcTable[(crc ^ head[i]) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        f.Write(head); f.Write(data);
        Span<byte> tail = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tail, crc ^ 0xFFFFFFFF);
        f.Write(tail);
    }

    /// <param name="px">BGRA, bottom row first, premultiplied by alpha.</param>
    static void WritePng(string path, byte[] px, int w, int h, bool alpha)
    {
        int bpp = alpha ? 4 : 3, stride = w * bpp + 1;
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] row = new byte[stride];
            for (int y = h - 1; y >= 0; y--)
            {
                int s = y * w * 4, d = 1;
                row[0] = 0;
                for (int x = 0; x < w; x++, s += 4)
                {
                    byte b = px[s], g = px[s + 1], r = px[s + 2], a = px[s + 3];
                    if (alpha && a != 255 && a != 0)        // PNG stores colour that has not been multiplied by alpha
                    {
                        r = (byte)Math.Min(255, r * 255 / a); g = (byte)Math.Min(255, g * 255 / a); b = (byte)Math.Min(255, b * 255 / a);
                    }
                    row[d++] = r; row[d++] = g; row[d++] = b;
                    if (alpha) row[d++] = a;
                }
                z.Write(row);
            }
        }
        using var f = File.Create(path);
        f.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)h);
        ihdr[8] = 8; ihdr[9] = (byte)(alpha ? 6 : 2); ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(f, "IHDR", ihdr);
        Chunk(f, "IDAT", packed.GetBuffer().AsSpan(0, (int)packed.Length));
        Chunk(f, "IEND", default);
    }

    // ───────────────────────── The Picture card ─────────────────────────

    /// <summary>The Picture button, top right. Returns false if there is no room for it.</summary>
    static void PictureButton(float s, float m, float toolbarRight)
    {
        const string label = "Picture";
        float h = 32 * s, pad = 14 * s, w = U.Measure(U.Medium, label) + pad * 2, x = _w - m - w, y = m;
        _picButton = default;
        if (x < toolbarRight + 12 * s) return;
        bool hot = _drag == 0 && _mouseX >= x && _mouseX <= x + w && _mouseY >= y && _mouseY <= y + h;
        if (hot) _hotButton = 9;
        if (_picture) U.Rect(x, y, w, h, h / 2, _theme.Accent);
        else
        {
            U.Rect(x, y, w, h, h / 2, hot ? new Vector4(_theme.Panel.X, _theme.Panel.Y, _theme.Panel.Z, _theme.Panel.W * 2.2f) : _theme.Panel);
            U.Rect(x, y, w, h, h / 2, _theme.PanelBorder, 1);
        }
        U.Text(U.Medium, label, x + pad, y + (h - U.Medium.Height) / 2, _picture ? AccentInk : _theme.Text);
        _picButton = (x, y, w, h);
        _buttons.Add((x, y, w, h, 9));
    }

    static void PictureCard(float s, float m)
    {
        _picButtons.Clear(); _hotPic = -1;
        float pad = 18 * s, w = 292 * s, rowH = 32 * s, gap = 8 * s;
        var (pw, ph, k) = PictureSize();
        float h = pad + U.Medium.Height + 14 * s + rowH + 18 * s + (U.Body.Height + 6 * s + rowH) * 2 + 32 * s + U.Body.Height * 2 + 4 * s + pad * 0.8f;
        float x = MathF.Max(8 * s, _w - m - w), y = m + 32 * s + 10 * s;
        Card(x, y, w, h, s);
        float ty = y + pad, inner = w - pad * 2;
        U.Text(U.Medium, "Picture", x + pad, ty, _theme.Text);
        string size = $"{pw:N0} × {ph:N0} px";
        U.Text(U.Body, size, x + w - pad - U.Measure(U.Body, size), ty + (U.Medium.Height - U.Body.Height) / 2, _theme.Dim);
        ty += U.Medium.Height + 14 * s;

        float half = (inner - gap) / 2;
        Button("Copy", x + pad, ty, half, false, 20);
        Button("Save…", x + pad + half + gap, ty, half, true, 21);
        ty += rowH + 18 * s;

        U.Text(U.Body, "Size", x + pad, ty, _theme.Dim); ty += U.Body.Height + 6 * s;
        float third = (inner - gap * 2) / 3;
        for (int i = 0; i < 3; i++)
        {
            int factor = i == 0 ? 1 : i == 1 ? 2 : 4;
            Choice(factor + "×", x + pad + i * (third + gap), ty, third, k == factor, 22 + i);
        }
        ty += rowH + 16 * s;

        U.Text(U.Body, "Background", x + pad, ty, _theme.Dim); ty += U.Body.Height + 6 * s;
        Choice("As shown", x + pad, ty, half, !Settings.PictureAlpha, 25);
        Choice("Transparent", x + pad + half + gap, ty, half, Settings.PictureAlpha, 26);
        ty += rowH + 8 * s + 8 * s;

        Vector4 faint = Fade(_theme.Dim, 0.8f);
        U.Text(U.Body, "No toolbar or readouts in the picture.", x + pad, ty, faint); ty += U.Body.Height + 4 * s;
        U.Text(U.Body, "Ctrl+C copies  ·  Ctrl+S saves", x + pad, ty, faint);

        bool Hot(float bx, float by, float bw, int action)
        {
            bool hot = _drag == 0 && _mouseX >= bx && _mouseX <= bx + bw && _mouseY >= by && _mouseY <= by + rowH;
            if (hot) _hotPic = action;
            _picButtons.Add((bx, by, bw, rowH, action));
            return hot;
        }
        void Button(string label, float bx, float by, float bw, bool primary, int action)
        {
            bool hot = Hot(bx, by, bw, action);
            if (primary) U.Rect(bx, by, bw, rowH, 9 * s, hot ? Fade(_theme.Accent, 0.88f) : _theme.Accent);
            else
            {
                U.Rect(bx, by, bw, rowH, 9 * s, hot ? new Vector4(_theme.Panel.X, _theme.Panel.Y, _theme.Panel.Z, _theme.Panel.W * 2.2f) : _theme.Panel);
                U.Rect(bx, by, bw, rowH, 9 * s, _theme.PanelBorder, 1);
            }
            U.TextCentered(U.Medium, label, bx + bw / 2, by + (rowH - U.Medium.Height) / 2, primary ? AccentInk : _theme.Text);
        }
        void Choice(string label, float bx, float by, float bw, bool on, int action)
        {
            bool hot = Hot(bx, by, bw, action);
            if (on) U.Rect(bx, by, bw, rowH, 9 * s, new Vector4(_theme.Accent.X, _theme.Accent.Y, _theme.Accent.Z, _theme.Dark ? 0.22f : 0.10f));
            else U.Rect(bx, by, bw, rowH, 9 * s, hot ? new Vector4(_theme.Panel.X, _theme.Panel.Y, _theme.Panel.Z, _theme.Panel.W * 2.2f) : _theme.Panel);
            U.Rect(bx, by, bw, rowH, 9 * s, on ? _theme.Accent : _theme.PanelBorder, on ? 1.5f * s : 1);
            U.TextCentered(on ? U.Medium : U.Body, label, bx + bw / 2, by + (rowH - (on ? U.Medium.Height : U.Body.Height)) / 2, _theme.Text);
        }
    }

    /// <summary>A click while the Picture card is open: its controls act, anywhere else closes it.</summary>
    static bool PictureClick(int mx, int my)
    {
        foreach (var b in _picButtons)
        {
            if (mx < b.X || mx > b.X + b.W || my < b.Y || my > b.Y + b.H) continue;
            switch (b.Action)
            {
                case 20: _picture = false; CopyImage(); break;
                case 21: _picture = false; SavePicture(); break;
                case 22: Settings.PictureScale = 1; break;
                case 23: Settings.PictureScale = 2; break;
                case 24: Settings.PictureScale = 4; break;
                case 25: Settings.PictureAlpha = false; break;
                case 26: Settings.PictureAlpha = true; break;
            }
            Invalidate();
            return true;
        }
        _picture = false; Invalidate();
        return true;
    }
}
