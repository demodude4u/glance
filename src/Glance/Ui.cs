using System.Numerics;
using static Glance.GL;

namespace Glance;

internal sealed class Face
{
    public struct Glyph { public float U0, V0, U1, V1, W, Adv; }
    public readonly Dictionary<char, Glyph> Glyphs = new();
    public int Height;
    public string Name = "";
    public const int Pad = 2;
}

/// <summary>HUD drawing: rounded rectangles, line segments and GDI-rasterised text in one batched draw call.</summary>
internal sealed unsafe class Ui
{
    const string Chars = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~×·°…²³←→–—ΔØ±";
    const int Stride = 14;

    public Face Body = new(), Medium = new(), Title = new();
    uint _prog, _vao, _vbo, _tex;
    float[] _v = new float[Stride * 6 * 512];
    int _n;
    int _w, _h;

    const string Vs = """
        #version 330 core
        in vec2 aPos; in vec2 aUv; in vec4 aCol; in vec4 aPar; in vec2 aExt;
        uniform vec2 uRes;
        out vec2 vUv; out vec4 vCol; out vec4 vPar; out vec2 vExt; out vec2 vPx;
        void main(){
          vUv = aUv; vCol = aCol; vPar = aPar; vExt = aExt; vPx = aPos;
          gl_Position = vec4(aPos.x / uRes.x * 2.0 - 1.0, 1.0 - aPos.y / uRes.y * 2.0, 0.0, 1.0);
        }
        """;
    const string Fs = """
        #version 330 core
        uniform sampler2D uTex;
        in vec2 vUv; in vec4 vCol; in vec4 vPar; in vec2 vExt; in vec2 vPx; out vec4 o;
        void main(){
          float a;
          if (vExt.x < 0.5) {
            vec2 q = abs(vUv) - vPar.xy + vExt.y;
            float d = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - vExt.y;
            a = clamp(0.5 - d, 0.0, 1.0);
            if (vPar.z > 0.0) a *= clamp(d + vPar.z + 0.5, 0.0, 1.0);
          } else if (vExt.x < 1.5) {
            a = texture(uTex, vUv).r;
          } else {
            vec2 pa = vPx - vPar.xy, ba = vPar.zw - vPar.xy;
            float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-6), 0.0, 1.0);
            a = clamp(vExt.y + 0.5 - length(pa - ba * h), 0.0, 1.0);
          }
          o = vec4(vCol.rgb, vCol.a * a);
        }
        """;

    public void Init()
    {
        _prog = Program(Vs, Fs, "aPos", "aUv", "aCol", "aPar", "aExt");
        _vao = GenVertexArray(); _vbo = GenBuffer();
        BindVertexArray(_vao); BindBuffer(ARRAY_BUFFER, _vbo);
        int s = Stride * 4;
        EnableVertexAttribArray(0); VertexAttribPointer(0, 2, FLOAT, 0, s, (void*)0);
        EnableVertexAttribArray(1); VertexAttribPointer(1, 2, FLOAT, 0, s, (void*)8);
        EnableVertexAttribArray(2); VertexAttribPointer(2, 4, FLOAT, 0, s, (void*)16);
        EnableVertexAttribArray(3); VertexAttribPointer(3, 4, FLOAT, 0, s, (void*)32);
        EnableVertexAttribArray(4); VertexAttribPointer(4, 2, FLOAT, 0, s, (void*)48);
        BindVertexArray(0);
    }

    byte[] _cov = [];
    int _aw, _ah;

    /// <summary>Rasterise the three faces with GDI into one coverage bitmap (no OpenGL needed yet).</summary>
    public void BakeFonts(float scale)
    {
        BundledFonts.Load();
        int aw = scale > 2.05f ? 2048 : 1024, ah = scale > 1.3f ? 1024 : 512;
        if (scale > 2.05f) ah = 2048;
        nint dc = Win32.CreateCompatibleDC(0);
        var bi = new Win32.BITMAPINFOHEADER { biSize = 40, biWidth = aw, biHeight = -ah, biPlanes = 1, biBitCount = 32 };
        void* bits;
        nint dib = Win32.CreateDIBSection(dc, &bi, 0, &bits, 0, 0);
        nint oldBmp = Win32.SelectObject(dc, dib);
        Win32.SetTextColor(dc, 0x00FFFFFF); Win32.SetBkMode(dc, 1);

        int x = 1, y = 1;
        Body = Bake(dc, [BundledFonts.Regular, "Roboto", "Segoe UI Variable Text", "Segoe UI"], 400, (int)MathF.Round(13.5f * scale), aw, ah, ref x, ref y);
        Medium = Bake(dc, [BundledFonts.Medium, "Roboto Medium", "Segoe UI Variable Text Semibold", "Segoe UI Semibold"], 500, (int)MathF.Round(13.5f * scale), aw, ah, ref x, ref y);
        Title = Bake(dc, [BundledFonts.Light, "Roboto Light", "Segoe UI Variable Display Light", "Segoe UI Light"], 300, (int)MathF.Round(27f * scale), aw, ah, ref x, ref y);
        Win32.GdiFlush();

        byte[] cov = new byte[aw * ah];
        byte* src = (byte*)bits;
        for (int i = 0; i < cov.Length; i++) cov[i] = src[i * 4 + 1];

        _cov = cov; _aw = aw; _ah = ah;
        Win32.SelectObject(dc, oldBmp); Win32.DeleteObject(dib); Win32.DeleteDC(dc);
    }

    public void UploadFonts()
    {
        byte[] cov = _cov; int aw = _aw, ah = _ah;
        if (cov.Length == 0) return;
        if (_tex == 0) { uint t; glGenTextures(1, &t); _tex = t; }
        glBindTexture(TEXTURE_2D, _tex);
        fixed (byte* p = cov) glTexImage2D(TEXTURE_2D, 0, (int)R8, aw, ah, 0, RED, UNSIGNED_BYTE, p);
        glTexParameteri(TEXTURE_2D, TEXTURE_MIN_FILTER, (int)LINEAR); glTexParameteri(TEXTURE_2D, TEXTURE_MAG_FILTER, (int)LINEAR);
        glTexParameteri(TEXTURE_2D, TEXTURE_WRAP_S, (int)CLAMP_TO_EDGE); glTexParameteri(TEXTURE_2D, TEXTURE_WRAP_T, (int)CLAMP_TO_EDGE);
        _cov = [];
    }

    static Face Bake(nint dc, string[] families, int weight, int px, int aw, int ah, ref int x, ref int y)
    {
        nint font = 0;
        char* got = stackalloc char[64];
        foreach (string fam in families)
        {
            if (font != 0) Win32.DeleteObject(font);
            fixed (char* pf = fam) font = Win32.CreateFontW(-px, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0, pf);
            Win32.SelectObject(dc, font);
            int n = Win32.GetTextFaceW(dc, 64, got);
            if (n > 0 && new string(got).Equals(fam, StringComparison.OrdinalIgnoreCase)) break;
        }
        int* tm = stackalloc int[24];
        Win32.GetTextMetricsW(dc, tm);
        Win32.GetTextFaceW(dc, 64, got);
        var face = new Face { Height = tm[0], Name = new string(got) };
        int pad = Face.Pad;
        foreach (char ch in Chars)
        {
            char c = ch; Win32.SIZE sz;
            Win32.GetTextExtentPoint32W(dc, &c, 1, &sz);
            int cw = sz.W + pad * 2;
            if (x + cw + 1 > aw) { x = 1; y += face.Height + 1; }
            if (y + face.Height + 1 > ah) break;
            Win32.TextOutW(dc, x + pad, y, &c, 1);
            face.Glyphs[ch] = new Face.Glyph
            {
                U0 = x / (float)aw, V0 = y / (float)ah, U1 = (x + cw) / (float)aw, V1 = (y + face.Height) / (float)ah,
                W = cw, Adv = sz.W,
            };
            x += cw + 1;
        }
        x = 1; y += face.Height + 1;
        Win32.SelectObject(dc, Win32.CreateFontW(12, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, null));
        Win32.DeleteObject(font);
        return face;
    }

    public void Begin(int w, int h) { _w = w; _h = h; _n = 0; }

    void Quad(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1, Vector4 c, Vector4 par, float mode, float ext)
    {
        if (_n + Stride * 6 > _v.Length) Array.Resize(ref _v, _v.Length * 2);
        Span<float> px = [x0, x1, x1, x0, x1, x0], py = [y0, y0, y1, y0, y1, y1];
        Span<float> pu = [u0, u1, u1, u0, u1, u0], pv = [v0, v0, v1, v0, v1, v1];
        for (int i = 0; i < 6; i++)
        {
            float[] v = _v; int n = _n;
            v[n] = px[i]; v[n + 1] = py[i]; v[n + 2] = pu[i]; v[n + 3] = pv[i];
            v[n + 4] = c.X; v[n + 5] = c.Y; v[n + 6] = c.Z; v[n + 7] = c.W;
            v[n + 8] = par.X; v[n + 9] = par.Y; v[n + 10] = par.Z; v[n + 11] = par.W;
            v[n + 12] = mode; v[n + 13] = ext;
            _n += Stride;
        }
    }

    /// <summary>Rounded rectangle. border &gt; 0 draws only an outline of that thickness.</summary>
    public void Rect(float x, float y, float w, float h, float radius, Vector4 color, float border = 0)
    {
        if (color.W <= 0 || w <= 0 || h <= 0) return;
        float hw = w / 2, hh = h / 2;
        radius = MathF.Min(radius, MathF.Min(hw, hh));
        Quad(x - 1, y - 1, x + w + 1, y + h + 1, -hw - 1, -hh - 1, hw + 1, hh + 1, color, new Vector4(hw, hh, border, 0), 0, radius);
    }

    public void Line(Vector2 a, Vector2 b, float halfWidth, Vector4 color)
    {
        float p = halfWidth + 2;
        Quad(MathF.Min(a.X, b.X) - p, MathF.Min(a.Y, b.Y) - p, MathF.Max(a.X, b.X) + p, MathF.Max(a.Y, b.Y) + p,
            0, 0, 0, 0, color, new Vector4(a.X, a.Y, b.X, b.Y), 2, halfWidth);
    }

    public float Measure(Face f, string s)
    {
        float w = 0;
        foreach (char ch in s) w += f.Glyphs.TryGetValue(ch, out var g) ? g.Adv : (f.Glyphs.TryGetValue('?', out g) ? g.Adv : 0);
        return w;
    }

    /// <summary>Draws text with its top-left at (x, y); returns the advance width.</summary>
    public float Text(Face f, string s, float x, float y, Vector4 color)
    {
        float start = x;
        x = MathF.Round(x); y = MathF.Round(y);
        foreach (char ch in s)
        {
            if (!f.Glyphs.TryGetValue(ch, out var g) && !f.Glyphs.TryGetValue('?', out g)) continue;
            if (ch != ' ')
                Quad(x - Face.Pad, y, x - Face.Pad + g.W, y + f.Height, g.U0, g.V0, g.U1, g.V1, color, default, 1, 0);
            x += g.Adv;
        }
        return x - MathF.Round(start);
    }

    public void TextCentered(Face f, string s, float cx, float y, Vector4 color) => Text(f, s, cx - Measure(f, s) / 2, y, color);

    public void Flush()
    {
        if (_n == 0) return;
        glViewport(0, 0, _w, _h);
        glDisable(DEPTH_TEST); glEnable(BLEND); glBlendFunc(SRC_ALPHA, ONE_MINUS_SRC_ALPHA);
        UseProgram(_prog);
        Uniform2f(Loc(_prog, "uRes"), _w, _h);
        Uniform1i(Loc(_prog, "uTex"), 0);
        ActiveTexture(TEXTURE0); glBindTexture(TEXTURE_2D, _tex);
        BindVertexArray(_vao); BindBuffer(ARRAY_BUFFER, _vbo);
        fixed (float* p = _v) BufferData(ARRAY_BUFFER, (nint)_n * 4, p, STREAM_DRAW);
        glDrawArrays(TRIANGLES, 0, _n / Stride);
        BindVertexArray(0);
        _n = 0;
    }
}
