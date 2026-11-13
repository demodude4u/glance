using System.Numerics;
using static Glance.GL;

namespace Glance;

internal sealed unsafe class Renderer
{
    uint _bg, _mesh, _line, _grid;
    uint _quadVao, _quadVbo;
    uint _meshVao, _vbPos, _vbNrm, _vbCol, _ibo;
    int _meshVerts, _meshIdx; bool _hasNrm, _hasCol;
    uint _edgeVao, _edgeVbo; int _edgeVerts;
    uint _hiIbo;
    const uint CLIP_DISTANCE0 = 0x3000;
    uint _fbo, _rbColor, _rbDepth; int _fbW, _fbH, _samples = 4;

    public bool HasMesh => _meshVerts > 0;
    public bool HasColors => _hasCol;
    public bool HasEdges => _edgeVerts > 0;

    const string BgVs = """
        #version 330 core
        in vec2 aPos; out vec2 uv;
        void main(){ uv = aPos*0.5+0.5; gl_Position = vec4(aPos, 0.0, 1.0); }
        """;
    const string BgFs = """
        #version 330 core
        uniform vec3 uCenter; uniform vec3 uEdge; uniform float uAspect;
        in vec2 uv; out vec4 o;
        void main(){
          vec2 q = uv - vec2(0.5, 0.56); q.x *= uAspect;
          float t = smoothstep(0.0, 0.95, length(q * vec2(0.85, 1.15)));
          t = max(t, smoothstep(0.70, 1.0, uv.y));
          vec3 c = mix(uCenter, uEdge, t);
          float n = fract(sin(dot(gl_FragCoord.xy, vec2(12.9898, 78.233))) * 43758.5453);
          o = vec4(c + (n - 0.5) / 255.0, 1.0);
        }
        """;

    const string MeshVs = """
        #version 330 core
        in vec3 aPos; in vec3 aNrm; in vec4 aCol;
        uniform mat4 uMVP; uniform mat4 uMV; uniform vec4 uClip;
        out vec3 vView; out vec3 vNrm; out vec4 vCol;
        void main(){
          gl_ClipDistance[0] = dot(vec4(aPos, 1.0), uClip);
          vec4 v = uMV * vec4(aPos, 1.0);
          vView = v.xyz; vNrm = mat3(uMV) * aNrm; vCol = aCol;
          gl_Position = uMVP * vec4(aPos, 1.0);
        }
        """;
    const string MeshFs = """
        #version 330 core
        uniform vec3 uBase; uniform float uFlat; uniform float uUseCol; uniform float uOrtho; uniform float uRim;
        uniform vec4 uCut;
        in vec3 vView; in vec3 vNrm; in vec4 vCol; out vec4 o;
        void main(){
          if (uCut.a > 0.5 && !gl_FrontFacing) {
            // Looking into the solid through the section plane: show the cut as a flat hatched surface.
            float h = step(0.5, fract((gl_FragCoord.x + gl_FragCoord.y) / 10.0));
            o = vec4(uCut.rgb * mix(0.86, 1.0, h), 1.0);
            return;
          }
          vec3 N = uFlat > 0.5 ? normalize(cross(dFdx(vView), dFdy(vView))) : normalize(vNrm);
          vec3 V = uOrtho > 0.5 ? vec3(0.0, 0.0, 1.0) : normalize(-vView);
          if (dot(N, V) < 0.0) N = -N;
          vec3 base = mix(uBase, vCol.rgb, vCol.a * uUseCol);
          vec3 L1 = normalize(vec3(0.45, 0.65, 0.62));
          vec3 L2 = normalize(vec3(-0.70, 0.15, 0.50));
          float key = max(dot(N, L1), 0.0);
          float fill = max(dot(N, L2), 0.0);
          float sky = N.y * 0.5 + 0.5;
          vec3 c = base * (0.40 + 0.16 * sky + 0.52 * key + 0.14 * fill);
          float spec = pow(max(dot(N, normalize(L1 + V)), 0.0), 56.0) * 0.16;
          float fres = pow(1.0 - max(dot(N, V), 0.0), 4.0) * uRim;
          o = vec4(c + spec + fres, 1.0);
        }
        """;

    const string LineVs = """
        #version 330 core
        in vec3 aPos; uniform mat4 uMVP; uniform vec4 uClip;
        void main(){ gl_ClipDistance[0] = dot(vec4(aPos, 1.0), uClip); gl_Position = uMVP * vec4(aPos, 1.0); }
        """;
    const string LineFs = """
        #version 330 core
        uniform vec4 uColor; out vec4 o;
        void main(){ o = uColor; }
        """;

    const string GridVs = """
        #version 330 core
        in vec2 aPos; uniform mat4 uVP; uniform vec3 uCenter; uniform float uExtent;
        out vec2 wp;
        void main(){ wp = uCenter.xy + aPos * uExtent; gl_Position = uVP * vec4(wp, uCenter.z, 1.0); }
        """;
    const string GridFs = """
        #version 330 core
        uniform vec3 uCenter; uniform float uRadius; uniform float uStep; uniform vec4 uColor; uniform float uShadow;
        in vec2 wp; out vec4 o;
        float lines(vec2 p, float s){
          vec2 c = p / s; vec2 w = fwidth(c);
          vec2 g = abs(fract(c - 0.5) - 0.5) / max(w, vec2(1e-6));
          float l = 1.0 - min(min(g.x, g.y), 1.0);
          return l * (1.0 - smoothstep(0.25, 0.7, max(w.x, w.y)));
        }
        void main(){
          float d = length(wp - uCenter.xy) / uRadius;
          float fade = 1.0 - smoothstep(1.3, 4.2, d);
          float a = (lines(wp, uStep) * 0.45 + lines(wp, uStep * 10.0) * 0.75) * fade * uColor.a;
          float sh = exp(-d * d * 1.9) * uShadow;
          o = vec4(uColor.rgb * a, a + sh * (1.0 - a));
        }
        """;

    public void Init()
    {
        _bg = Program(BgVs, BgFs, "aPos");
        _mesh = Program(MeshVs, MeshFs, "aPos", "aNrm", "aCol");
        _line = Program(LineVs, LineFs, "aPos");
        _grid = Program(GridVs, GridFs, "aPos");

        _quadVao = GenVertexArray(); _quadVbo = GenBuffer();
        BindVertexArray(_quadVao); BindBuffer(ARRAY_BUFFER, _quadVbo);
        float* q = stackalloc float[12] { -1, -1, 1, -1, 1, 1, -1, -1, 1, 1, -1, 1 };
        BufferData(ARRAY_BUFFER, 48, q, STATIC_DRAW);
        EnableVertexAttribArray(0); VertexAttribPointer(0, 2, FLOAT, 0, 0, null);

        _meshVao = GenVertexArray(); _vbPos = GenBuffer(); _vbNrm = GenBuffer(); _vbCol = GenBuffer(); _ibo = GenBuffer();
        _edgeVao = GenVertexArray(); _edgeVbo = GenBuffer(); _hiIbo = GenBuffer();
        BindVertexArray(0);

        int max; glGetIntegerv(MAX_SAMPLES, &max);
        _samples = Math.Clamp(max, 1, 4);
        glPixelStorei(UNPACK_ALIGNMENT, 1); glPixelStorei(PACK_ALIGNMENT, 1);
    }

    public void Upload(Mesh m)
    {
        BindVertexArray(_meshVao);
        BindBuffer(ARRAY_BUFFER, _vbPos);
        fixed (float* p = m.Pos) BufferData(ARRAY_BUFFER, (nint)m.Pos.Length * 4, p, STATIC_DRAW);
        EnableVertexAttribArray(0); VertexAttribPointer(0, 3, FLOAT, 0, 0, null);

        _hasNrm = m.Nrm != null;
        if (_hasNrm)
        {
            BindBuffer(ARRAY_BUFFER, _vbNrm);
            fixed (float* p = m.Nrm) BufferData(ARRAY_BUFFER, (nint)m.Nrm!.Length * 4, p, STATIC_DRAW);
            EnableVertexAttribArray(1); VertexAttribPointer(1, 3, FLOAT, 0, 0, null);
        }
        else { BindBuffer(ARRAY_BUFFER, _vbNrm); BufferData(ARRAY_BUFFER, 0, null, STATIC_DRAW); DisableVertexAttribArray(1); VertexAttrib4f(1, 0, 0, 1, 0); }

        _hasCol = m.Col != null;
        if (_hasCol)
        {
            BindBuffer(ARRAY_BUFFER, _vbCol);
            fixed (uint* p = m.Col) BufferData(ARRAY_BUFFER, (nint)m.Col!.Length * 4, p, STATIC_DRAW);
            EnableVertexAttribArray(2); VertexAttribPointer(2, 4, UNSIGNED_BYTE, 1, 0, null);
        }
        else { BindBuffer(ARRAY_BUFFER, _vbCol); BufferData(ARRAY_BUFFER, 0, null, STATIC_DRAW); DisableVertexAttribArray(2); VertexAttrib4f(2, 0, 0, 0, 0); }

        BindBuffer(ELEMENT_ARRAY_BUFFER, _ibo);
        if (m.Idx != null) { fixed (uint* p = m.Idx) BufferData(ELEMENT_ARRAY_BUFFER, (nint)m.Idx.Length * 4, p, STATIC_DRAW); _meshIdx = m.Idx.Length; }
        else { BufferData(ELEMENT_ARRAY_BUFFER, 0, null, STATIC_DRAW); _meshIdx = 0; }
        _meshVerts = m.VertexCount;
        BindVertexArray(0);
        UploadEdges(null);
    }

    /// <summary>Swap in (or drop, with null) a normal buffer for the current mesh.</summary>
    public void UploadNormals(float[]? nrm)
    {
        if (_meshVerts == 0) return;
        BindVertexArray(_meshVao); BindBuffer(ARRAY_BUFFER, _vbNrm);
        if (nrm != null)
        {
            fixed (float* p = nrm) BufferData(ARRAY_BUFFER, (nint)nrm.Length * 4, p, STATIC_DRAW);
            EnableVertexAttribArray(1); VertexAttribPointer(1, 3, FLOAT, 0, 0, null);
        }
        else { BufferData(ARRAY_BUFFER, 0, null, STATIC_DRAW); DisableVertexAttribArray(1); VertexAttrib4f(1, 0, 0, 1, 0); }
        _hasNrm = nrm != null;
        BindVertexArray(0);
    }

    public void UploadEdges(float[]? lines)
    {
        BindVertexArray(_edgeVao); BindBuffer(ARRAY_BUFFER, _edgeVbo);
        if (lines != null) fixed (float* p = lines) BufferData(ARRAY_BUFFER, (nint)lines.Length * 4, p, STATIC_DRAW);
        else BufferData(ARRAY_BUFFER, 0, null, STATIC_DRAW);
        EnableVertexAttribArray(0); VertexAttribPointer(0, 3, FLOAT, 0, 0, null);
        _edgeVerts = lines?.Length / 3 ?? 0;
        BindVertexArray(0);
    }

    public void Clear() { _meshVerts = 0; _meshIdx = 0; _edgeVerts = 0; }

    public void Begin(int w, int h)
    {
        if (w != _fbW || h != _fbH || _fbo == 0)
        {
            if (_fbo == 0) { uint f; GenFramebuffers(1, &f); _fbo = f; }
            uint* rb = stackalloc uint[2] { _rbColor, _rbDepth };
            if (_rbColor != 0) DeleteRenderbuffers(2, rb);
            GenRenderbuffers(2, rb); _rbColor = rb[0]; _rbDepth = rb[1];
            BindRenderbuffer(RENDERBUFFER, _rbColor); RenderbufferStorageMultisample(RENDERBUFFER, _samples, RGBA8, w, h);
            BindRenderbuffer(RENDERBUFFER, _rbDepth); RenderbufferStorageMultisample(RENDERBUFFER, _samples, DEPTH_COMPONENT24, w, h);
            BindFramebuffer(FRAMEBUFFER, _fbo);
            FramebufferRenderbuffer(FRAMEBUFFER, COLOR_ATTACHMENT0, RENDERBUFFER, _rbColor);
            FramebufferRenderbuffer(FRAMEBUFFER, DEPTH_ATTACHMENT, RENDERBUFFER, _rbDepth);
            _fbW = w; _fbH = h;
        }
        BindFramebuffer(FRAMEBUFFER, _fbo);
        glViewport(0, 0, w, h);
        glDepthMask(1);
        glClear(DEPTH_BUFFER_BIT);
    }

    public void Background(Vector3 center, Vector3 edge, float aspect)
    {
        glDisable(DEPTH_TEST); glDisable(BLEND);
        UseProgram(_bg);
        Uniform3f(Loc(_bg, "uCenter"), center.X, center.Y, center.Z);
        Uniform3f(Loc(_bg, "uEdge"), edge.X, edge.Y, edge.Z);
        Uniform1f(Loc(_bg, "uAspect"), aspect);
        BindVertexArray(_quadVao);
        glDrawArrays(TRIANGLES, 0, 6);
    }

    static void Mat(uint prog, string name, Matrix4x4 m) => UniformMatrix4fv(Loc(prog, name), 1, 0, (float*)&m);

    void DrawGeometry()
    {
        if (_meshIdx > 0) glDrawElements(TRIANGLES, _meshIdx, UNSIGNED_INT, null);
        else glDrawArrays(TRIANGLES, 0, _meshVerts - _meshVerts % 3);
    }

    static void Clip(uint prog, Vector4? clip)
    {
        Vector4 c = clip ?? new Vector4(0, 0, 0, 1);
        Uniform4f(Loc(prog, "uClip"), c.X, c.Y, c.Z, c.W);
    }

    /// <summary>Tints a set of triangles of the current mesh (a hovered or selected face).</summary>
    public void Highlight(Matrix4x4 mvp, uint[] tris, Vector4 color, Vector4? clip)
    {
        if (_meshVerts == 0 || tris.Length == 0) return;
        glEnable(DEPTH_TEST); glDepthFunc(LEQUAL); glDepthMask(0);
        glEnable(BLEND); glBlendFunc(SRC_ALPHA, ONE_MINUS_SRC_ALPHA);
        if (clip.HasValue) glEnable(CLIP_DISTANCE0);
        UseProgram(_line);
        Mat(_line, "uMVP", mvp); Clip(_line, clip);
        Uniform4f(Loc(_line, "uColor"), color.X, color.Y, color.Z, color.W);
        BindVertexArray(_meshVao);
        BindBuffer(ELEMENT_ARRAY_BUFFER, _hiIbo);
        fixed (uint* p = tris) BufferData(ELEMENT_ARRAY_BUFFER, (nint)tris.Length * 4, p, STREAM_DRAW);
        glDrawElements(TRIANGLES, tris.Length, UNSIGNED_INT, null);
        BindBuffer(ELEMENT_ARRAY_BUFFER, _ibo);
        BindVertexArray(0);
        glDisable(CLIP_DISTANCE0);
        glDepthMask(1);
    }

    public void Model(Matrix4x4 mv, Matrix4x4 mvp, Vector3 baseColor, bool useFileColors, bool ortho, float rim,
        bool edges, bool wire, Vector4 edgeColor, float lineWidth, Vector4? clip, Vector3 cutColor)
    {
        if (_meshVerts == 0) return;
        glEnable(DEPTH_TEST); glDepthFunc(LEQUAL); glDisable(BLEND);
        if (clip.HasValue) glEnable(CLIP_DISTANCE0);
        glEnable(POLYGON_OFFSET_FILL); glPolygonOffset(1f, 1f);
        UseProgram(_mesh);
        Mat(_mesh, "uMV", mv); Mat(_mesh, "uMVP", mvp);
        Uniform3f(Loc(_mesh, "uBase"), baseColor.X, baseColor.Y, baseColor.Z);
        Uniform1f(Loc(_mesh, "uFlat"), _hasNrm ? 0 : 1);
        Uniform1f(Loc(_mesh, "uUseCol"), useFileColors && _hasCol ? 1 : 0);
        Uniform1f(Loc(_mesh, "uOrtho"), ortho ? 1 : 0);
        Uniform1f(Loc(_mesh, "uRim"), rim);
        Uniform4f(Loc(_mesh, "uCut"), cutColor.X, cutColor.Y, cutColor.Z, clip.HasValue ? 1 : 0);
        Clip(_mesh, clip);
        BindVertexArray(_meshVao);
        DrawGeometry();
        glDisable(POLYGON_OFFSET_FILL);

        if (!edges && !wire) { glDisable(CLIP_DISTANCE0); return; }
        glEnable(BLEND); glBlendFunc(SRC_ALPHA, ONE_MINUS_SRC_ALPHA);
        glDepthMask(0);
        glLineWidth(lineWidth);
        UseProgram(_line);
        Mat(_line, "uMVP", mvp); Clip(_line, clip);
        if (wire)
        {
            Uniform4f(Loc(_line, "uColor"), edgeColor.X, edgeColor.Y, edgeColor.Z, edgeColor.W * 0.55f);
            glPolygonMode(FRONT_AND_BACK, LINE);
            DrawGeometry();
            glPolygonMode(FRONT_AND_BACK, FILL);
        }
        if (edges && _edgeVerts > 0)
        {
            Uniform4f(Loc(_line, "uColor"), edgeColor.X, edgeColor.Y, edgeColor.Z, edgeColor.W);
            BindVertexArray(_edgeVao);
            glDrawArrays(LINES, 0, _edgeVerts);
        }
        glDisable(CLIP_DISTANCE0);
        glDepthMask(1);
    }

    public void Grid(Matrix4x4 vp, Vector3 center, float radius, float step, Vector4 color, float shadow)
    {
        glEnable(DEPTH_TEST); glEnable(BLEND); glBlendFunc(ONE, ONE_MINUS_SRC_ALPHA);
        glDepthMask(0);
        UseProgram(_grid);
        Mat(_grid, "uVP", vp);
        Uniform3f(Loc(_grid, "uCenter"), center.X, center.Y, center.Z);
        Uniform1f(Loc(_grid, "uRadius"), radius);
        Uniform1f(Loc(_grid, "uExtent"), radius * 4.5f);
        Uniform1f(Loc(_grid, "uStep"), step);
        Uniform4f(Loc(_grid, "uColor"), color.X, color.Y, color.Z, color.W);
        Uniform1f(Loc(_grid, "uShadow"), shadow);
        BindVertexArray(_quadVao);
        glDrawArrays(TRIANGLES, 0, 6);
        glDepthMask(1);
    }

    /// <summary>Resolve the multisampled scene to the window and leave the window bound for the HUD.</summary>
    public void Resolve(int w, int h)
    {
        BindFramebuffer(READ_FRAMEBUFFER, _fbo);
        BindFramebuffer(DRAW_FRAMEBUFFER, 0);
        BlitFramebuffer(0, 0, w, h, 0, 0, w, h, COLOR_BUFFER_BIT, NEAREST);
        BindFramebuffer(FRAMEBUFFER, 0);
        glDisable(DEPTH_TEST);
    }
}
