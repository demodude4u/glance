using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;
using static Glance.Win32;

namespace Glance;

/// <summary>
/// Pictures of the model: the Picture card (copy, save, size, background), rendering at a named size such as
/// Full HD whatever the window's shape, and writing PNG files. A picture never includes the toolbar, readouts or cards.
/// </summary>
internal static unsafe partial class App
{
    static bool _picture;                   // the Picture card is open
    static bool _shotAlpha;                 // the frame being rendered has no backdrop
    static float _shotK = 1;                // the frame being rendered has this many pixels for each one in the window
    static float _shotCrop = 1;             // ...and shows this much of the window's height (the framed area)
    static int _hotPic = -1;
    static int _gpuMax, _testMax;           // the largest picture the graphics card can draw; a test's stand-in for it
    static (float X, float Y, float W, float H) _picButton, _picCard;
    static readonly List<(float X, float Y, float W, float H, int Action)> _picButtons = new();

    /// The sizes on offer. Window is the view exactly as it is; the others have their own shape.
    static readonly (string Name, string Key, int W, int H)[] PictureSizes =
    [
        ("Window", "window", 0, 0), ("HD", "hd", 1280, 720), ("Full HD", "fullhd", 1920, 1080),
        ("QHD", "qhd", 2560, 1440), ("4K", "4k", 3840, 2160), ("Square", "square", 2048, 2048),
    ];

    static void TogglePicture()
    {
        if (_mesh == null) return;
        _picture = !_picture;
        if (_picture) { _fixUnits = false; _help = false; }
    }

    static int PictureChoice()
    {
        for (int i = 0; i < PictureSizes.Length; i++) if (PictureSizes[i].Key == Settings.PictureSize) return i;
        return 2;
    }

    static int PictureLimit() => _testMax > 0 ? _testMax : _gpuMax > 0 ? _gpuMax : _gpuMax = Math.Min(R.MaxSize, 8192);

    /// <summary>The picture's size in pixels. Limited is true when the graphics card cannot draw the size that was asked for.</summary>
    static (int W, int H, bool Limited) PictureSize()
    {
        var c = PictureSizes[PictureChoice()];
        int w = c.W > 0 ? c.W : _w, h = c.W > 0 ? c.H : _h, max = PictureLimit();
        if (w <= max && h <= max) return (w, h, false);
        double k = Math.Min(max / (double)w, max / (double)h);          // same shape, as large as the card allows
        return (Math.Clamp((int)Math.Round(w * k), 1, max), Math.Clamp((int)Math.Round(h * k), 1, max), true);
    }

    /// <summary>The part of the window that goes into the picture: the largest centred area with the picture's shape.</summary>
    static (float X, float Y, float W, float H) PictureFrame()
    {
        if (PictureChoice() == 0) return (0, 0, _w, _h);
        var (pw, ph, _) = PictureSize();
        float shape = pw / (float)ph, fw = _w, fh = _w / shape;
        if (fh > _h) { fh = _h; fw = _h * shape; }
        return ((_w - fw) / 2, (_h - fh) / 2, fw, fh);
    }

    /// <summary>Renders the framed part of the current view without any overlay. BGRA, bottom row first; premultiplied when transparent.</summary>
    static byte[] RenderPicture(bool transparent, out int w, out int h)
    {
        var (pw, ph, _) = PictureSize();
        var frame = PictureFrame();
        int ow = _w, oh = _h;
        _shotCrop = frame.H / oh; _shotK = ph / frame.H;
        _w = pw; _h = ph; _shotAlpha = transparent;
        try
        {
            Render(hud: false, toWindow: false);
            w = pw; h = ph;
            return R.ReadResolved(pw, ph);
        }
        finally { _w = ow; _h = oh; _shotAlpha = false; _shotK = 1; _shotCrop = 1; Invalidate(); }
    }

    static long _frameFlash;                // Ctrl+C or Ctrl+S just took a picture: show its frame for a moment
    static bool _frameHold;                 // ...and for as long as the Save dialog is open
    const double FrameFlashSeconds = 1.2;

    static bool FrameShowing => _picture || _frameHold || (_frameFlash != 0 && Seconds(_frameFlash) < FrameFlashSeconds);

    /// <summary>
    /// Dims what will not be in the picture and outlines what will. Shown while the Picture card is open, and
    /// briefly when a picture is taken from the keyboard, so the frame is never a surprise.
    /// </summary>
    static void PictureFrameOverlay(float s)
    {
        if (PictureChoice() == 0) return;
        var f = PictureFrame();
        Vector4 dim = _sceneDark ? new Vector4(0, 0, 0, 0.42f) : new Vector4(0.30f, 0.34f, 0.40f, 0.26f);
        if (f.Y >= 1)
        {
            U.Rect(0, 0, _w, f.Y, 0, dim);
            U.Rect(0, f.Y + f.H, _w, _h - f.Y - f.H, 0, dim);
        }
        if (f.X >= 1)
        {
            U.Rect(0, 0, f.X, _h, 0, dim);
            U.Rect(f.X + f.W, 0, _w - f.X - f.W, _h, 0, dim);
        }
        U.Rect(f.X, f.Y, f.W, f.H, 0, _sceneDark ? new Vector4(1, 1, 1, 0.40f) : new Vector4(0.2f, 0.2f, 0.2f, 0.30f), MathF.Max(1, s));
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
            Toast($"Picture copied  ·  {w} × {h}");
            _frameFlash = System.Diagnostics.Stopwatch.GetTimestamp();
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
                _frameHold = true; Frame();                 // the frame stays up behind the dialog
                bool ok = GetSaveFileNameW(&ofn) != 0;
                _frameHold = false; _drag = 0; Invalidate();
                if (!ok) return;
            }
            path = new string(buf);
            _frameFlash = System.Diagnostics.Stopwatch.GetTimestamp();
        }
        bool transparent = Settings.PictureAlpha;
        byte[] px = RenderPicture(transparent, out int w, out int h);
        string file = path;
        void Write()
        {
            try
            {
                WritePng(file, px, w, h, transparent);
                Post(() => Toast($"Saved {Path.GetFileName(file)}  ·  {w} × {h}", 3f));
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
        else Pill(x, y, w, h, hot);
        U.Text(U.Medium, label, x + pad, y + (h - U.Medium.Height) / 2, _picture ? AccentInk : _theme.Text);
        _picButton = (x, y, w, h);
        _buttons.Add((x, y, w, h, 9));
    }

    static void PictureCard(float s, float m)
    {
        _picButtons.Clear(); _hotPic = -1;
        float pad = 18 * s, w = 324 * s, rowH = 32 * s, sizeH = 48 * s, gap = 8 * s;
        var (pw, ph, limited) = PictureSize();
        int choice = PictureChoice();
        float h = pad + U.Medium.Height + 14 * s + rowH + 18 * s + U.Body.Height + 6 * s + sizeH * 2 + gap + (limited ? U.Body.Height + 8 * s : 0) + 16 * s
            + U.Body.Height + 6 * s + rowH + 16 * s + U.Body.Height * 2 + 4 * s + pad * 0.8f;
        float x = MathF.Max(8 * s, _w - m - w), y = m + 32 * s + 10 * s;
        if (_toolbarWrapRight > x - 12 * s) y = MathF.Max(y, _toolbarBottom + 10 * s);  // below a wrapped toolbar that reaches this far
        if (_optRight > x - 12 * s) y = MathF.Max(y, _optBottom + 10 * s);              // and below the mode's options when they do
        if (_sliderBox.W > 0 && _sliderBox.X + _sliderBox.W > x - 12 * s) y = MathF.Max(y, _sliderBox.Y + _sliderBox.H + 10 * s);
        // In a short window, showing the whole card matters more than keeping clear of the rows above it
        // (they do nothing while the card is open); it still stays under the Picture button.
        y = MathF.Max(MathF.Min(y, _h - h - 8 * s), m + 32 * s + 10 * s);
        Card(x, y, w, h, s);
        _picCard = (x, y, w, h);
        float ty = y + pad, inner = w - pad * 2;
        U.Text(U.Medium, "Picture", x + pad, ty, _theme.Text);
        string size = $"{pw} × {ph} px";
        U.Text(U.Body, size, x + w - pad - U.Measure(U.Body, size), ty + (U.Medium.Height - U.Body.Height) / 2, _theme.Dim);
        ty += U.Medium.Height + 14 * s;

        float half = (inner - gap) / 2;
        Button("Copy", x + pad, ty, half, false, 20);
        Button("Save…", x + pad + half + gap, ty, half, true, 21);
        ty += rowH + 18 * s;

        U.Text(U.Body, "Size", x + pad, ty, _theme.Dim); ty += U.Body.Height + 6 * s;
        float third = (inner - gap * 2) / 3;
        for (int i = 0; i < PictureSizes.Length; i++)
        {
            var p = PictureSizes[i];
            string px = p.W > 0 ? $"{p.W} × {p.H}" : $"{_w} × {_h}";
            Choice(p.Name, px, x + pad + i % 3 * (third + gap), ty + i / 3 * (sizeH + gap), third, sizeH, i == choice, 22 + i);
        }
        ty += sizeH * 2 + gap;
        if (limited)
        {
            ty += 8 * s;
            U.Text(U.Body, Fit(U.Body, $"This graphics card stops at {pw} × {ph}.", inner), x + pad, ty, _theme.Text);
            ty += U.Body.Height;
        }
        ty += 16 * s;

        U.Text(U.Body, "Background", x + pad, ty, _theme.Dim); ty += U.Body.Height + 6 * s;
        Choice("As shown", null, x + pad, ty, half, rowH, !Settings.PictureAlpha, 28);
        Choice("Transparent", null, x + pad + half + gap, ty, half, rowH, Settings.PictureAlpha, 29);
        ty += rowH + 16 * s;

        Vector4 faint = Fade(_theme.Dim, 0.8f);
        string what = choice == 0 ? "No toolbar or readouts in the picture." : "The picture is what the frame shows.";
        U.Text(U.Body, Fit(U.Body, what, inner), x + pad, ty, faint); ty += U.Body.Height + 4 * s;
        U.Text(U.Body, "Ctrl+C copies  ·  Ctrl+S saves", x + pad, ty, faint);

        bool Hot(float bx, float by, float bw, float bh, int action)
        {
            bool hot = _drag == 0 && _mouseX >= bx && _mouseX <= bx + bw && _mouseY >= by && _mouseY <= by + bh;
            if (hot) _hotPic = action;
            _picButtons.Add((bx, by, bw, bh, action));
            return hot;
        }
        void Button(string label, float bx, float by, float bw, bool primary, int action)
        {
            bool hot = Hot(bx, by, bw, rowH, action);
            if (primary) U.Rect(bx, by, bw, rowH, 9 * s, hot ? Fade(_theme.Accent, 0.88f) : _theme.Accent);
            else
            {
                U.Rect(bx, by, bw, rowH, 9 * s, hot ? new Vector4(_theme.Panel.X, _theme.Panel.Y, _theme.Panel.Z, _theme.Panel.W * 2.2f) : _theme.Panel);
                U.Rect(bx, by, bw, rowH, 9 * s, _theme.PanelBorder, 1);
            }
            U.TextCentered(U.Medium, label, bx + bw / 2, by + (rowH - U.Medium.Height) / 2, primary ? AccentInk : _theme.Text);
        }
        // One of a set of choices. With a second line (the size in pixels) the name sits above it.
        void Choice(string label, string? sub, float bx, float by, float bw, float bh, bool on, int action)
        {
            bool hot = Hot(bx, by, bw, bh, action);
            if (on) U.Rect(bx, by, bw, bh, 9 * s, new Vector4(_theme.Accent.X, _theme.Accent.Y, _theme.Accent.Z, _theme.Dark ? 0.22f : 0.10f));
            else U.Rect(bx, by, bw, bh, 9 * s, hot ? new Vector4(_theme.Panel.X, _theme.Panel.Y, _theme.Panel.Z, _theme.Panel.W * 2.2f) : _theme.Panel);
            U.Rect(bx, by, bw, bh, 9 * s, on ? _theme.Accent : _theme.PanelBorder, on ? 1.5f * s : 1);
            Face face = on ? U.Medium : U.Body;
            if (sub == null) { U.TextCentered(face, label, bx + bw / 2, by + (bh - face.Height) / 2, _theme.Text); return; }
            float top = by + (bh - face.Height - U.Body.Height + 2 * s) / 2;
            U.TextCentered(face, label, bx + bw / 2, top, _theme.Text);
            U.TextCentered(U.Body, sub, bx + bw / 2, top + face.Height - 2 * s, Fade(_theme.Dim, on ? 1 : 0.8f));
        }
    }

    /// <summary>
    /// A press while the Picture card is open. Its controls act. A press on the view is left to start a drag, so the
    /// model can still be turned inside the frame; a plain click there closes the card (see WM_LBUTTONUP).
    /// </summary>
    static bool PictureClick(int mx, int my)
    {
        foreach (var b in _picButtons)
        {
            if (mx < b.X || mx > b.X + b.W || my < b.Y || my > b.Y + b.H) continue;
            switch (b.Action)
            {
                case 20: _picture = false; CopyImage(); break;
                case 21: _picture = false; SavePicture(); break;
                case >= 22 and < 28: Settings.PictureSize = PictureSizes[b.Action - 22].Key; break;
                case 28: Settings.PictureAlpha = false; break;
                case 29: Settings.PictureAlpha = true; break;
            }
            Invalidate();
            return true;
        }
        return mx >= _picCard.X && mx <= _picCard.X + _picCard.W && my >= _picCard.Y && my <= _picCard.Y + _picCard.H;
    }
}
