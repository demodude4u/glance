using System.Runtime.InteropServices;
using System.Text;

namespace Glance;

/// <summary>Minimal OpenGL binding: GL 1.1 from opengl32.dll, the rest through wglGetProcAddress.</summary>
internal static unsafe class GL
{
    const string L = "opengl32";

    public const uint COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x100, TRIANGLES = 4, LINES = 1,
        DEPTH_TEST = 0x0B71, BLEND = 0x0BE2, SRC_ALPHA = 0x302, ONE_MINUS_SRC_ALPHA = 0x303, ONE = 1,
        FLOAT = 0x1406, UNSIGNED_BYTE = 0x1401, UNSIGNED_INT = 0x1405,
        ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893, STATIC_DRAW = 0x88E4, STREAM_DRAW = 0x88E0,
        VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30, COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82,
        FRAMEBUFFER = 0x8D40, READ_FRAMEBUFFER = 0x8CA8, DRAW_FRAMEBUFFER = 0x8CA9, RENDERBUFFER = 0x8D41,
        COLOR_ATTACHMENT0 = 0x8CE0, DEPTH_ATTACHMENT = 0x8D00, RGBA8 = 0x8058, DEPTH_COMPONENT24 = 0x81A6,
        TEXTURE_2D = 0x0DE1, TEXTURE0 = 0x84C0, R8 = 0x8229, RED = 0x1903, LINEAR = 0x2601,
        TEXTURE_MIN_FILTER = 0x2801, TEXTURE_MAG_FILTER = 0x2800, TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803,
        CLAMP_TO_EDGE = 0x812F, UNPACK_ALIGNMENT = 0x0CF5, PACK_ALIGNMENT = 0x0D05,
        POLYGON_OFFSET_FILL = 0x8037, FRONT_AND_BACK = 0x0408, LINE = 0x1B01, FILL = 0x1B02,
        LEQUAL = 0x0203, BGRA = 0x80E1, BACK = 0x0405, NEAREST = 0x2600, MAX_SAMPLES = 0x8D57,
        CULL_FACE = 0x0B44, MULTISAMPLE = 0x809D;

    [DllImport(L)] public static extern void glClear(uint mask);
    [DllImport(L)] public static extern void glClearColor(float r, float g, float b, float a);
    [DllImport(L)] public static extern void glViewport(int x, int y, int w, int h);
    [DllImport(L)] public static extern void glEnable(uint cap);
    [DllImport(L)] public static extern void glDisable(uint cap);
    [DllImport(L)] public static extern void glBlendFunc(uint s, uint d);
    [DllImport(L)] public static extern void glDepthFunc(uint f);
    [DllImport(L)] public static extern void glDepthMask(byte flag);
    [DllImport(L)] public static extern void glDrawArrays(uint mode, int first, int count);
    [DllImport(L)] public static extern void glDrawElements(uint mode, int count, uint type, void* indices);
    [DllImport(L)] public static extern void glGenTextures(int n, uint* t);
    [DllImport(L)] public static extern void glDeleteTextures(int n, uint* t);
    [DllImport(L)] public static extern void glBindTexture(uint target, uint t);
    [DllImport(L)] public static extern void glTexImage2D(uint target, int level, int ifmt, int w, int h, int border, uint fmt, uint type, void* data);
    [DllImport(L)] public static extern void glTexParameteri(uint target, uint name, int value);
    [DllImport(L)] public static extern void glPixelStorei(uint name, int value);
    [DllImport(L)] public static extern void glPolygonOffset(float factor, float units);
    [DllImport(L)] public static extern void glPolygonMode(uint face, uint mode);
    [DllImport(L)] public static extern void glLineWidth(float w);
    [DllImport(L)] public static extern void glReadPixels(int x, int y, int w, int h, uint fmt, uint type, void* data);
    [DllImport(L)] public static extern void glReadBuffer(uint mode);
    [DllImport(L)] public static extern void glDrawBuffer(uint mode);
    [DllImport(L)] public static extern void glGetIntegerv(uint name, int* v);
    [DllImport(L)] public static extern void glFinish();
    [DllImport(L)] public static extern nint glGetString(uint name);

    public static delegate* unmanaged<uint, uint> CreateShader;
    public static delegate* unmanaged<uint, int, byte**, int*, void> ShaderSource;
    public static delegate* unmanaged<uint, void> CompileShader;
    public static delegate* unmanaged<uint, uint, int*, void> GetShaderiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetShaderInfoLog;
    public static delegate* unmanaged<uint, void> DeleteShader;
    public static delegate* unmanaged<uint> CreateProgram;
    public static delegate* unmanaged<uint, uint, void> AttachShader;
    public static delegate* unmanaged<uint, uint, byte*, void> BindAttribLocation;
    public static delegate* unmanaged<uint, void> LinkProgram;
    public static delegate* unmanaged<uint, uint, int*, void> GetProgramiv;
    public static delegate* unmanaged<uint, int, int*, byte*, void> GetProgramInfoLog;
    public static delegate* unmanaged<uint, void> UseProgram;
    public static delegate* unmanaged<uint, byte*, int> GetUniformLocation;
    public static delegate* unmanaged<int, int, void> Uniform1i;
    public static delegate* unmanaged<int, float, void> Uniform1f;
    public static delegate* unmanaged<int, float, float, void> Uniform2f;
    public static delegate* unmanaged<int, float, float, float, void> Uniform3f;
    public static delegate* unmanaged<int, float, float, float, float, void> Uniform4f;
    public static delegate* unmanaged<int, int, byte, float*, void> UniformMatrix4fv;
    public static delegate* unmanaged<int, uint*, void> GenBuffers;
    public static delegate* unmanaged<int, uint*, void> DeleteBuffers;
    public static delegate* unmanaged<uint, uint, void> BindBuffer;
    public static delegate* unmanaged<uint, nint, void*, uint, void> BufferData;
    public static delegate* unmanaged<int, uint*, void> GenVertexArrays;
    public static delegate* unmanaged<uint, void> BindVertexArray;
    public static delegate* unmanaged<uint, void> EnableVertexAttribArray;
    public static delegate* unmanaged<uint, void> DisableVertexAttribArray;
    public static delegate* unmanaged<uint, int, uint, byte, int, void*, void> VertexAttribPointer;
    public static delegate* unmanaged<uint, float, float, float, float, void> VertexAttrib4f;
    public static delegate* unmanaged<int, uint*, void> GenFramebuffers;
    public static delegate* unmanaged<uint, uint, void> BindFramebuffer;
    public static delegate* unmanaged<uint, uint, uint, uint, void> FramebufferRenderbuffer;
    public static delegate* unmanaged<int, uint*, void> GenRenderbuffers;
    public static delegate* unmanaged<int, uint*, void> DeleteRenderbuffers;
    public static delegate* unmanaged<uint, uint, void> BindRenderbuffer;
    public static delegate* unmanaged<uint, int, uint, int, int, void> RenderbufferStorageMultisample;
    public static delegate* unmanaged<int, int, int, int, int, int, int, int, uint, uint, void> BlitFramebuffer;
    public static delegate* unmanaged<uint, void> ActiveTexture;
    public static delegate* unmanaged<uint, uint, uint, uint, int, void> FramebufferTexture2D;
    public static delegate* unmanaged<uint, void> GenerateMipmap;
    public static delegate* unmanaged<int, uint*, void> DeleteFramebuffers;
    public static delegate* unmanaged<uint, uint, uint, uint, void> BlendFuncSeparate;
    public static delegate* unmanaged<int, int> SwapInterval;

    static nint P(string name, bool required = true)
    {
        byte[] b = Encoding.ASCII.GetBytes(name + "\0");
        nint p;
        fixed (byte* pb = b) p = Win32.wglGetProcAddress(pb);
        if (required && (p == 0 || p == 1 || p == 2 || p == 3 || p == -1))
            throw new InvalidOperationException("Your graphics driver is missing OpenGL 3.3 support (" + name + ").");
        return p;
    }

    public static void Load()
    {
        CreateShader = (delegate* unmanaged<uint, uint>)P("glCreateShader");
        ShaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)P("glShaderSource");
        CompileShader = (delegate* unmanaged<uint, void>)P("glCompileShader");
        GetShaderiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetShaderiv");
        GetShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetShaderInfoLog");
        DeleteShader = (delegate* unmanaged<uint, void>)P("glDeleteShader");
        CreateProgram = (delegate* unmanaged<uint>)P("glCreateProgram");
        AttachShader = (delegate* unmanaged<uint, uint, void>)P("glAttachShader");
        BindAttribLocation = (delegate* unmanaged<uint, uint, byte*, void>)P("glBindAttribLocation");
        LinkProgram = (delegate* unmanaged<uint, void>)P("glLinkProgram");
        GetProgramiv = (delegate* unmanaged<uint, uint, int*, void>)P("glGetProgramiv");
        GetProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)P("glGetProgramInfoLog");
        UseProgram = (delegate* unmanaged<uint, void>)P("glUseProgram");
        GetUniformLocation = (delegate* unmanaged<uint, byte*, int>)P("glGetUniformLocation");
        Uniform1i = (delegate* unmanaged<int, int, void>)P("glUniform1i");
        Uniform1f = (delegate* unmanaged<int, float, void>)P("glUniform1f");
        Uniform2f = (delegate* unmanaged<int, float, float, void>)P("glUniform2f");
        Uniform3f = (delegate* unmanaged<int, float, float, float, void>)P("glUniform3f");
        Uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)P("glUniform4f");
        UniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)P("glUniformMatrix4fv");
        GenBuffers = (delegate* unmanaged<int, uint*, void>)P("glGenBuffers");
        DeleteBuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteBuffers");
        BindBuffer = (delegate* unmanaged<uint, uint, void>)P("glBindBuffer");
        BufferData = (delegate* unmanaged<uint, nint, void*, uint, void>)P("glBufferData");
        GenVertexArrays = (delegate* unmanaged<int, uint*, void>)P("glGenVertexArrays");
        BindVertexArray = (delegate* unmanaged<uint, void>)P("glBindVertexArray");
        EnableVertexAttribArray = (delegate* unmanaged<uint, void>)P("glEnableVertexAttribArray");
        DisableVertexAttribArray = (delegate* unmanaged<uint, void>)P("glDisableVertexAttribArray");
        VertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, void*, void>)P("glVertexAttribPointer");
        VertexAttrib4f = (delegate* unmanaged<uint, float, float, float, float, void>)P("glVertexAttrib4f");
        GenFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glGenFramebuffers");
        BindFramebuffer = (delegate* unmanaged<uint, uint, void>)P("glBindFramebuffer");
        FramebufferRenderbuffer = (delegate* unmanaged<uint, uint, uint, uint, void>)P("glFramebufferRenderbuffer");
        GenRenderbuffers = (delegate* unmanaged<int, uint*, void>)P("glGenRenderbuffers");
        DeleteRenderbuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteRenderbuffers");
        BindRenderbuffer = (delegate* unmanaged<uint, uint, void>)P("glBindRenderbuffer");
        RenderbufferStorageMultisample = (delegate* unmanaged<uint, int, uint, int, int, void>)P("glRenderbufferStorageMultisample");
        BlitFramebuffer = (delegate* unmanaged<int, int, int, int, int, int, int, int, uint, uint, void>)P("glBlitFramebuffer");
        ActiveTexture = (delegate* unmanaged<uint, void>)P("glActiveTexture");
        FramebufferTexture2D = (delegate* unmanaged<uint, uint, uint, uint, int, void>)P("glFramebufferTexture2D");
        GenerateMipmap = (delegate* unmanaged<uint, void>)P("glGenerateMipmap");
        DeleteFramebuffers = (delegate* unmanaged<int, uint*, void>)P("glDeleteFramebuffers");
        BlendFuncSeparate = (delegate* unmanaged<uint, uint, uint, uint, void>)P("glBlendFuncSeparate");
        SwapInterval = (delegate* unmanaged<int, int>)P("wglSwapIntervalEXT", required: false);
    }

    public static uint GenBuffer() { uint b; GenBuffers(1, &b); return b; }
    public static uint GenVertexArray() { uint b; GenVertexArrays(1, &b); return b; }

    public static uint Program(string vs, string fs, params string[] attribs)
    {
        uint v = Compile(VERTEX_SHADER, vs), f = Compile(FRAGMENT_SHADER, fs);
        uint p = CreateProgram();
        AttachShader(p, v); AttachShader(p, f);
        for (uint i = 0; i < attribs.Length; i++)
        {
            byte[] n = Encoding.ASCII.GetBytes(attribs[i] + "\0");
            fixed (byte* pn = n) BindAttribLocation(p, i, pn);
        }
        LinkProgram(p);
        int ok; GetProgramiv(p, LINK_STATUS, &ok);
        if (ok == 0)
        {
            byte* log = stackalloc byte[2048]; int len;
            GetProgramInfoLog(p, 2048, &len, log);
            throw new InvalidOperationException("Shader link failed: " + Encoding.ASCII.GetString(log, Math.Max(len, 0)));
        }
        DeleteShader(v); DeleteShader(f);
        return p;
    }

    static uint Compile(uint type, string src)
    {
        uint s = CreateShader(type);
        byte[] b = Encoding.ASCII.GetBytes(src);
        fixed (byte* pb = b)
        {
            byte* ptr = pb; int len = b.Length;
            ShaderSource(s, 1, &ptr, &len);
        }
        CompileShader(s);
        int ok; GetShaderiv(s, COMPILE_STATUS, &ok);
        if (ok == 0)
        {
            byte* log = stackalloc byte[2048]; int len;
            GetShaderInfoLog(s, 2048, &len, log);
            throw new InvalidOperationException("Shader compile failed: " + Encoding.ASCII.GetString(log, Math.Max(len, 0)));
        }
        return s;
    }

    static readonly Dictionary<(uint, string), int> _locs = new();

    public static int Loc(uint prog, string name)
    {
        if (_locs.TryGetValue((prog, name), out int loc)) return loc;
        byte[] n = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* pn = n) loc = GetUniformLocation(prog, pn);
        _locs[(prog, name)] = loc;
        return loc;
    }
}
