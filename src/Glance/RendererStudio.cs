using System.Numerics;
using static Glance.GL;

namespace Glance;

/// <summary>
/// The two "render" looks, Studio (matte clay) and Metal, and what they need: a depth pre-pass for soft corner
/// shadows, a top-down shadow map for the soft shadow on the ground, and reading back a picture larger than
/// the window. Everything here is created the first time it is used, so it costs nothing at start-up.
/// </summary>
internal sealed unsafe partial class Renderer
{
    uint _render, _floor;
    uint _depthFbo, _depthTex; int _depthW, _depthH;
    uint _shadowFbo, _shadowTex;
    const int ShadowSize = 512;
    Matrix4x4 _studioProj; bool _studioOrtho; float _aoRadius; bool _studioDark;

    const uint DEPTH_COMPONENT = 0x1902, TEXTURE1 = 0x84C1, TEXTURE2 = 0x84C2, LINEAR_MIPMAP_LINEAR = 0x2703,
        MAX_TEXTURE_SIZE = 0x0D33, MAX_RENDERBUFFER_SIZE = 0x84E8;

    const string RenderFs = """
        #version 330 core
        uniform vec3 uBase; uniform float uFlat; uniform float uUseCol; uniform float uOrtho; uniform float uStyle;
        uniform vec4 uCut; uniform sampler2D uDepth; uniform vec4 uProj; uniform vec2 uScreen; uniform float uAoR; uniform float uDark;
        uniform vec3 uLight; uniform vec4 uLightMix;
        in vec3 vView; in vec3 vNrm; in vec4 vCol; out vec4 o;

        // View-space position of whatever the depth pre-pass saw at this screen position.
        vec3 seen(vec2 uv){
          float ndc = texture(uDepth, uv).r * 2.0 - 1.0;
          vec2 q = uv * 2.0 - 1.0;
          if (uOrtho > 0.5) { float z = (ndc - uProj.w) / uProj.z; return vec3(q.x / uProj.x, q.y / uProj.y, z); }
          float z = -uProj.w / (ndc + uProj.z);
          return vec3(q.x * -z / uProj.x, q.y * -z / uProj.y, z);
        }

        // How open the surface is to its surroundings: 1 in the open, lower in corners, holes and against the ground.
        float openness(vec3 P, vec3 N){
          vec2 uv = gl_FragCoord.xy * uScreen;
          float r = uOrtho > 0.5 ? uAoR * uProj.y * 0.5 : uAoR * uProj.y * 0.5 / max(-P.z, 1e-6);
          r = clamp(r, 3.0 * uScreen.y, 0.16);
          float n = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
          float s = 0.0;
          for (int i = 0; i < 16; i++) {
            float t = (float(i) + 0.5) / 16.0, a = 6.2831853 * (n + float(i) * 0.618034);
            vec2 d = vec2(cos(a), sin(a)) * r * sqrt(t);
            d.x *= uScreen.x / uScreen.y;
            vec3 v = (seen(uv + d) - P) / uAoR;
            s += max(0.0, dot(N, v) - 0.04) / (dot(v, v) + 0.05);
          }
          float open = clamp(1.0 - 1.9 * s / 16.0, 0.0, 1.0);
          return open * open * (3.0 - 2.0 * open);
        }

        // A photo studio seen in reflection: dark floor, grey walls, a large soft light overhead and a strip to one side.
        vec3 studio(vec3 R){
          vec3 e = mix(vec3(0.17), vec3(0.52), smoothstep(-0.65, 0.15, R.y));
          e = mix(e, vec3(1.02), smoothstep(0.30, 0.78, R.y));
          e += vec3(0.85) * smoothstep(0.80, 0.95, dot(R, normalize(vec3(-0.85, 0.22, 0.42))));
          e += vec3(0.30) * smoothstep(0.88, 0.98, dot(R, normalize(vec3(0.80, -0.05, 0.55))));
          return e;
        }

        void main(){
          if (uCut.a > 0.5 && !gl_FrontFacing) {
            float h = step(0.5, fract((gl_FragCoord.x + gl_FragCoord.y) / 10.0));
            o = vec4(uCut.rgb * mix(0.86, 1.0, h), 1.0);
            return;
          }
          vec3 N = uFlat > 0.5 ? normalize(cross(dFdx(vView), dFdy(vView))) : normalize(vNrm);
          vec3 V = uOrtho > 0.5 ? vec3(0.0, 0.0, 1.0) : normalize(-vView);
          if (dot(N, V) < 0.0) N = -N;
          float open = openness(vView, N);
          vec3 base = mix(uBase, vCol.rgb, vCol.a * uUseCol);
          if (uStyle < 4.5) {
            // Studio: matte clay under a soft sky with one broad light, no highlights.
            // uLight is where the light is; uLightMix is how much comes from all round, from the sky and from the
            // light, and how far the light wraps round the part (0 = a hard side light, more = softer).
            float sky = N.y * 0.5 + 0.5;
            float key = max(dot(N, uLight) * (1.0 - uLightMix.w) + uLightMix.w, 0.0);
            vec3 c = base * (uLightMix.x + uLightMix.y * sky + uLightMix.z * key);
            vec3 shade = mix(vec3(0.16, 0.15, 0.19), vec3(1.0), open);
            o = vec4(c * shade, 1.0);
          } else {
            // Metal: mostly a reflection of the studio, slightly softened, brighter at grazing angles.
            float nv = max(dot(N, V), 0.0);
            vec3 R = reflect(-V, N);
            vec3 env = mix(studio(R), studio(normalize(R + N * 0.7)), 0.45);
            vec3 F = base + (vec3(1.0) - base) * pow(1.0 - nv, 5.0);
            vec3 c = env * F * mix(0.22, 1.0, open) + base * 0.04;
            o = vec4(c, 1.0);
          }
        }
        """;

    const string FloorVs = """
        #version 330 core
        in vec2 aPos; uniform mat4 uVP; uniform vec3 uCenter; uniform float uExtent;
        out vec2 uv;
        void main(){ uv = aPos * 0.5 + 0.5; gl_Position = uVP * vec4(uCenter.xy + aPos * uExtent, uCenter.z, 1.0); }
        """;
    const string FloorFs = """
        #version 330 core
        uniform sampler2D uShadow; uniform float uStrength;
        in vec2 uv; out vec4 o;
        float tap(float lod, float r){
          return (textureLod(uShadow, uv + vec2(r, r * 0.4), lod).r + textureLod(uShadow, uv + vec2(-r * 0.4, r), lod).r
                + textureLod(uShadow, uv + vec2(-r, -r * 0.4), lod).r + textureLod(uShadow, uv + vec2(r * 0.4, -r), lod).r) * 0.25;
        }
        void main(){
          // A tight shadow where the part meets the ground, fading into a wide, faint one.
          float a = 0.55 * tap(2.0, 0.004) + 0.35 * tap(3.5, 0.014) + 0.30 * tap(5.0, 0.040);
          vec2 e = smoothstep(0.0, 0.15, uv) * smoothstep(0.0, 0.15, 1.0 - uv);
          a = clamp(a, 0.0, 1.0) * uStrength * e.x * e.y;
          o = vec4(0.0, 0.0, 0.0, a);
        }
        """;

    /// <summary>The largest picture this graphics card can render in one piece.</summary>
    public int MaxSize
    {
        get
        {
            int t, r; glGetIntegerv(MAX_TEXTURE_SIZE, &t); glGetIntegerv(MAX_RENDERBUFFER_SIZE, &r);
            return Math.Max(1024, Math.Min(t, r));
        }
    }

    uint RenderProgram() => _render != 0 ? _render : _render = Program(MeshVs, RenderFs, "aPos", "aNrm", "aCol");

    int _studioLight;

    /// <param name="light">Studio's light: 0 soft, 1 from the left, 2 from the right, 3 from above.</param>
    public void SetStudio(Matrix4x4 proj, bool ortho, float aoRadius, bool dark, int light = 0)
    {
        _studioProj = proj; _studioOrtho = ortho; _aoRadius = aoRadius; _studioDark = dark; _studioLight = light;
    }

    void StudioUniforms(uint p)
    {
        // Where the light is, as seen from the camera (x right, y up, z towards the viewer), and its mix:
        // all-round light, sky light, the light itself, and how far it wraps round the part.
        var (dir, mix) = _studioLight switch
        {
            1 => (new Vector3(-0.82f, 0.34f, 0.46f), new Vector4(0.19f, 0.20f, 0.70f, 0.10f)),
            2 => (new Vector3(0.82f, 0.34f, 0.46f), new Vector4(0.19f, 0.20f, 0.70f, 0.10f)),
            3 => (new Vector3(0.00f, 0.93f, 0.36f), new Vector4(0.19f, 0.16f, 0.74f, 0.10f)),
            _ => (new Vector3(0.40f, 0.70f, 0.60f), new Vector4(0.26f, 0.34f, 0.46f, 0.35f)),
        };
        dir = Vector3.Normalize(dir);
        Uniform3f(Loc(p, "uLight"), dir.X, dir.Y, dir.Z);
        Uniform4f(Loc(p, "uLightMix"), mix.X, mix.Y, mix.Z, mix.W);
        Matrix4x4 m = _studioProj;
        Uniform4f(Loc(p, "uProj"), m.M11, m.M22, m.M33, m.M43);
        Uniform2f(Loc(p, "uScreen"), 1f / _fbW, 1f / _fbH);
        Uniform1f(Loc(p, "uAoR"), _aoRadius);
        Uniform1f(Loc(p, "uDark"), _studioDark ? 1 : 0);
        ActiveTexture(TEXTURE1); glBindTexture(TEXTURE_2D, _depthTex);
        Uniform1i(Loc(p, "uDepth"), 1);
        ActiveTexture(TEXTURE0);
    }

    /// <summary>Depth only, at the size of the picture: what the soft corner shadows look around in.</summary>
    public void Prepass(Matrix4x4 mvp, Vector4? clip, int w, int h, Matrix4x4? floor)
    {
        if (_meshVerts == 0) return;
        if (_depthFbo == 0 || w != _depthW || h != _depthH)
        {
            if (_depthFbo == 0) { uint f; GenFramebuffers(1, &f); _depthFbo = f; uint t; glGenTextures(1, &t); _depthTex = t; }
            glBindTexture(TEXTURE_2D, _depthTex);
            glTexImage2D(TEXTURE_2D, 0, (int)DEPTH_COMPONENT24, w, h, 0, DEPTH_COMPONENT, UNSIGNED_INT, null);
            glTexParameteri(TEXTURE_2D, TEXTURE_MIN_FILTER, (int)NEAREST); glTexParameteri(TEXTURE_2D, TEXTURE_MAG_FILTER, (int)NEAREST);
            glTexParameteri(TEXTURE_2D, TEXTURE_WRAP_S, (int)CLAMP_TO_EDGE); glTexParameteri(TEXTURE_2D, TEXTURE_WRAP_T, (int)CLAMP_TO_EDGE);
            BindFramebuffer(FRAMEBUFFER, _depthFbo);
            FramebufferTexture2D(FRAMEBUFFER, DEPTH_ATTACHMENT, TEXTURE_2D, _depthTex, 0);
            glDrawBuffer(0); glReadBuffer(0);
            _depthW = w; _depthH = h;
        }
        BindFramebuffer(FRAMEBUFFER, _depthFbo);
        glViewport(0, 0, w, h);
        glDepthMask(1); glClear(DEPTH_BUFFER_BIT);
        glEnable(DEPTH_TEST); glDepthFunc(LEQUAL); glDisable(BLEND);
        UseProgram(_line);
        Mat(_line, "uMVP", mvp); Clip(_line, clip);
        if (clip.HasValue) glEnable(CLIP_DISTANCE0);
        BindVertexArray(_meshVao);
        DrawGeometry();
        glDisable(CLIP_DISTANCE0);
        if (floor.HasValue)          // the ground counts as a surface too, so the part darkens where it meets it
        {
            Mat(_line, "uMVP", floor.Value); Clip(_line, null);
            BindVertexArray(_quadVao);
            glDrawArrays(TRIANGLES, 0, 6);
        }
    }

    /// <summary>The part's outline seen from straight above, blurred later into the shadow on the ground.</summary>
    public void ShadowMap(Matrix4x4 fromAbove, Vector4? clip)
    {
        if (_meshVerts == 0) return;
        if (_shadowFbo == 0)
        {
            uint f; GenFramebuffers(1, &f); _shadowFbo = f; uint t; glGenTextures(1, &t); _shadowTex = t;
            glBindTexture(TEXTURE_2D, _shadowTex);
            glTexImage2D(TEXTURE_2D, 0, (int)RGBA8, ShadowSize, ShadowSize, 0, BGRA, UNSIGNED_BYTE, null);
            glTexParameteri(TEXTURE_2D, TEXTURE_MIN_FILTER, (int)LINEAR_MIPMAP_LINEAR); glTexParameteri(TEXTURE_2D, TEXTURE_MAG_FILTER, (int)LINEAR);
            glTexParameteri(TEXTURE_2D, TEXTURE_WRAP_S, (int)CLAMP_TO_EDGE); glTexParameteri(TEXTURE_2D, TEXTURE_WRAP_T, (int)CLAMP_TO_EDGE);
            BindFramebuffer(FRAMEBUFFER, _shadowFbo);
            FramebufferTexture2D(FRAMEBUFFER, COLOR_ATTACHMENT0, TEXTURE_2D, _shadowTex, 0);
        }
        BindFramebuffer(FRAMEBUFFER, _shadowFbo);
        glViewport(0, 0, ShadowSize, ShadowSize);
        glClearColor(0, 0, 0, 0); glClear(COLOR_BUFFER_BIT);
        glDisable(DEPTH_TEST); glDisable(BLEND);
        UseProgram(_line);
        Mat(_line, "uMVP", fromAbove); Clip(_line, clip);
        Uniform4f(Loc(_line, "uColor"), 1, 1, 1, 1);
        if (clip.HasValue) glEnable(CLIP_DISTANCE0);
        BindVertexArray(_meshVao);
        DrawGeometry();
        glDisable(CLIP_DISTANCE0);
        glBindTexture(TEXTURE_2D, _shadowTex);
        GenerateMipmap(TEXTURE_2D);
    }

    /// <summary>Draws only the shadow: the ground itself stays invisible, so it works on any backdrop.</summary>
    public void Floor(Matrix4x4 vp, Vector3 center, float extent, float strength)
    {
        if (_shadowTex == 0) return;
        if (_floor == 0) _floor = Program(FloorVs, FloorFs, "aPos");
        glEnable(DEPTH_TEST); glDepthFunc(LEQUAL); glDepthMask(0);
        glEnable(BLEND); glBlendFunc(ONE, ONE_MINUS_SRC_ALPHA);
        UseProgram(_floor);
        Mat(_floor, "uVP", vp);
        Uniform3f(Loc(_floor, "uCenter"), center.X, center.Y, center.Z);
        Uniform1f(Loc(_floor, "uExtent"), extent);
        Uniform1f(Loc(_floor, "uStrength"), strength);
        ActiveTexture(TEXTURE2); glBindTexture(TEXTURE_2D, _shadowTex);
        Uniform1i(Loc(_floor, "uShadow"), 2);
        ActiveTexture(TEXTURE0);
        BindVertexArray(_quadVao);
        glDrawArrays(TRIANGLES, 0, 6);
        glDepthMask(1);
    }

    /// <summary>The finished frame as BGRA, bottom row first, at the size it was rendered (which may exceed the window).</summary>
    public byte[] ReadResolved(int w, int h)
    {
        uint f, rb; GenFramebuffers(1, &f); GenRenderbuffers(1, &rb);
        BindRenderbuffer(RENDERBUFFER, rb); RenderbufferStorageMultisample(RENDERBUFFER, 0, RGBA8, w, h);
        BindFramebuffer(FRAMEBUFFER, f);
        FramebufferRenderbuffer(FRAMEBUFFER, COLOR_ATTACHMENT0, RENDERBUFFER, rb);
        BindFramebuffer(READ_FRAMEBUFFER, _fbo); BindFramebuffer(DRAW_FRAMEBUFFER, f);
        BlitFramebuffer(0, 0, w, h, 0, 0, w, h, COLOR_BUFFER_BIT, NEAREST);
        BindFramebuffer(READ_FRAMEBUFFER, f);
        glReadBuffer(COLOR_ATTACHMENT0);
        byte[] px = new byte[(long)w * h * 4];
        fixed (byte* p = px) glReadPixels(0, 0, w, h, BGRA, UNSIGNED_BYTE, p);
        BindFramebuffer(FRAMEBUFFER, 0);
        DeleteFramebuffers(1, &f); DeleteRenderbuffers(1, &rb);
        return px;
    }
}
