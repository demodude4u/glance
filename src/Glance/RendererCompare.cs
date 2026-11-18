using System.Numerics;
using static Glance.GL;

namespace Glance;

/// <summary>
/// What comparing two models needs from the renderer: a second set of mesh buffers, so that flipping between the
/// two is a swap of names and not an upload, and the overlay, which draws both and colours each pixel by whether
/// the two surfaces agree there. Everything here is created the first time it is used.
/// </summary>
internal sealed unsafe partial class Renderer
{
    /// <summary>One model's buffers on the graphics card.</summary>
    struct Slot
    {
        public uint Vao, Pos, Nrm, Col, Ibo, EdgeVao, EdgeVbo;
        public int Verts, Idx, EdgeVerts;
        public bool HasNrm, HasCol;
    }

    Slot _spare;            // the model that is not on screen
    bool _spareMade;
    uint _overlay;
    readonly uint[] _ovFbo = new uint[2], _ovTex = new uint[2];
    int _ovW, _ovH;

    public bool HasNormals => _hasNrm;
    public bool HasSpare => _spare.Verts > 0;

    /// <summary>The model on screen and the one held back change places. Nothing is uploaded.</summary>
    public void SwapSlots()
    {
        if (!_spareMade)
        {
            _spare = new Slot
            {
                Vao = GenVertexArray(), Pos = GenBuffer(), Nrm = GenBuffer(), Col = GenBuffer(), Ibo = GenBuffer(),
                EdgeVao = GenVertexArray(), EdgeVbo = GenBuffer(),
            };
            _spareMade = true;
        }
        var shown = new Slot
        {
            Vao = _meshVao, Pos = _vbPos, Nrm = _vbNrm, Col = _vbCol, Ibo = _ibo, EdgeVao = _edgeVao, EdgeVbo = _edgeVbo,
            Verts = _meshVerts, Idx = _meshIdx, EdgeVerts = _edgeVerts, HasNrm = _hasNrm, HasCol = _hasCol,
        };
        Slot s = _spare;
        _meshVao = s.Vao; _vbPos = s.Pos; _vbNrm = s.Nrm; _vbCol = s.Col; _ibo = s.Ibo; _edgeVao = s.EdgeVao; _edgeVbo = s.EdgeVbo;
        _meshVerts = s.Verts; _meshIdx = s.Idx; _edgeVerts = s.EdgeVerts; _hasNrm = s.HasNrm; _hasCol = s.HasCol;
        _spare = shown;
    }

    /// <summary>Gives back the memory of the model that is not on screen, and of the overlay's own buffers.</summary>
    public void ReleaseSpare()
    {
        if (!_spareMade) return;
        foreach (uint b in (ReadOnlySpan<uint>)[_spare.Pos, _spare.Nrm, _spare.Col, _spare.Ibo, _spare.EdgeVbo])
        {
            BindBuffer(ARRAY_BUFFER, b);
            BufferData(ARRAY_BUFFER, 0, null, STATIC_DRAW);
        }
        BindBuffer(ARRAY_BUFFER, 0);
        _spare.Verts = _spare.Idx = _spare.EdgeVerts = 0; _spare.HasNrm = _spare.HasCol = false;
        if (_ovTex[0] != 0)
        {
            for (int i = 0; i < 2; i++)
            {
                glBindTexture(TEXTURE_2D, _ovTex[i]);
                glTexImage2D(TEXTURE_2D, 0, (int)DEPTH_COMPONENT24, 1, 1, 0, DEPTH_COMPONENT, UNSIGNED_INT, null);
            }
            _ovW = _ovH = 0;
        }
    }

    void DrawSlot(int which)
    {
        if (which == 0) { BindVertexArray(_meshVao); DrawGeometry(); return; }
        BindVertexArray(_spare.Vao);
        if (_spare.Idx > 0) glDrawElements(TRIANGLES, _spare.Idx, UNSIGNED_INT, null);
        else glDrawArrays(TRIANGLES, 0, _spare.Verts - _spare.Verts % 3);
    }

    /// <summary>
    /// The overlay's shader. Both models' depths as seen from the camera are rendered first. Each model is then
    /// drawn, and every pixel of it asks: is the other model's nearest surface here within the tolerance of mine?
    /// If so the pixel is neutral, if not it takes this model's colour. The question is put to the 3 x 3 pixels
    /// around it, counting only those that are still on this same surface, and the answer is the share that agree:
    /// that gives the boundary between colours a smooth edge and keeps a shared outline free of a coloured fringe.
    /// The tolerance widens on surfaces seen edge-on, where a small gap is a long way along the line of sight.
    /// </summary>
    const string OverlayFs = """
        #version 330 core
        uniform sampler2D uSelf; uniform sampler2D uOther; uniform vec4 uProj; uniform float uOrtho; uniform float uTol;
        uniform vec3 uOwn; uniform vec3 uShared; uniform vec4 uCut;
        in vec3 vView; in vec3 vNrm; in vec4 vCol; out vec4 o;

        float viewZ(sampler2D t, ivec2 q){
          float ndc = texelFetch(t, q, 0).r * 2.0 - 1.0;
          return uOrtho > 0.5 ? (ndc - uProj.w) / uProj.z : -uProj.w / (ndc + uProj.z);
        }

        void main(){
          vec3 N = normalize(cross(dFdx(vView), dFdy(vView)));
          vec3 V = uOrtho > 0.5 ? vec3(0.0, 0.0, 1.0) : normalize(-vView);
          if (dot(N, V) < 0.0) N = -N;
          float tol = uTol / max(dot(N, V), 0.14);
          float gx = dFdx(vView.z), gy = dFdy(vView.z);
          ivec2 c = ivec2(gl_FragCoord.xy), top = textureSize(uOther, 0) - 1;
          float mine = 0.0, agree = 0.0;
          for (int j = -1; j <= 1; j++) for (int i = -1; i <= 1; i++) {
            ivec2 q = clamp(c + ivec2(i, j), ivec2(0), top);
            float a = viewZ(uSelf, q);
            if (abs(a - (vView.z + gx * float(i) + gy * float(j))) > tol) continue;     // not this surface any more
            mine += 1.0;
            if (abs(viewZ(uOther, q) - a) <= tol) agree += 1.0;
          }
          float same = mine > 0.5 ? agree / mine : step(abs(viewZ(uOther, clamp(c, ivec2(0), top)) - vView.z), tol);
          vec3 base = mix(uOwn, uShared, smoothstep(0.25, 0.75, same));

          vec3 L1 = normalize(vec3(0.45, 0.65, 0.62));
          vec3 L2 = normalize(vec3(-0.70, 0.15, 0.50));
          float key = max(dot(N, L1), 0.0), fill = max(dot(N, L2), 0.0), sky = N.y * 0.5 + 0.5;
          vec3 col = base * (0.42 + 0.16 * sky + 0.50 * key + 0.14 * fill);
          if (uCut.a > 0.5 && !gl_FrontFacing) {
            // Seen through the section plane: the inside, flat and faintly hatched like any other cut.
            float h = step(0.5, fract((gl_FragCoord.x + gl_FragCoord.y) / 10.0));
            col = mix(base, uCut.rgb, 0.45) * mix(0.90, 1.0, h);
          }
          o = vec4(col, 1.0);
        }
        """;

    /// <summary>Depth of one of the two models (0 the one on screen, 1 the other) as seen from the camera.</summary>
    public void OverlayDepth(int which, Matrix4x4 mvp, Vector4? clip, int w, int h)
    {
        if (_ovFbo[0] == 0 || w != _ovW || h != _ovH)
        {
            for (int i = 0; i < 2; i++)
            {
                if (_ovFbo[i] == 0) { uint f; GenFramebuffers(1, &f); _ovFbo[i] = f; uint t; glGenTextures(1, &t); _ovTex[i] = t; }
                glBindTexture(TEXTURE_2D, _ovTex[i]);
                glTexImage2D(TEXTURE_2D, 0, (int)DEPTH_COMPONENT24, w, h, 0, DEPTH_COMPONENT, UNSIGNED_INT, null);
                glTexParameteri(TEXTURE_2D, TEXTURE_MIN_FILTER, (int)NEAREST); glTexParameteri(TEXTURE_2D, TEXTURE_MAG_FILTER, (int)NEAREST);
                glTexParameteri(TEXTURE_2D, TEXTURE_WRAP_S, (int)CLAMP_TO_EDGE); glTexParameteri(TEXTURE_2D, TEXTURE_WRAP_T, (int)CLAMP_TO_EDGE);
                BindFramebuffer(FRAMEBUFFER, _ovFbo[i]);
                FramebufferTexture2D(FRAMEBUFFER, DEPTH_ATTACHMENT, TEXTURE_2D, _ovTex[i], 0);
                glDrawBuffer(0); glReadBuffer(0);
            }
            _ovW = w; _ovH = h;
        }
        BindFramebuffer(FRAMEBUFFER, _ovFbo[which]);
        glViewport(0, 0, w, h);
        glDepthMask(1); glClear(DEPTH_BUFFER_BIT);
        glEnable(DEPTH_TEST); glDepthFunc(LEQUAL); glDisable(BLEND);
        UseProgram(_line);
        Mat(_line, "uMVP", mvp); Clip(_line, clip);
        if (clip.HasValue) glEnable(CLIP_DISTANCE0);
        DrawSlot(which);
        glDisable(CLIP_DISTANCE0);
        BindVertexArray(0);
    }

    /// <summary>
    /// One of the two models into the frame, coloured against the other's depth. Call for both after Begin.
    /// </summary>
    /// <param name="tolerance">How far apart, in the model's units, two surfaces may be and still count as the same.</param>
    public void OverlayDraw(int which, Matrix4x4 mv, Matrix4x4 mvp, Matrix4x4 proj, bool ortho, Vector4? clip,
        float tolerance, Vector3 own, Vector3 shared, Vector3 cutColor)
    {
        if (_overlay == 0) _overlay = Program(MeshVs, OverlayFs, "aPos", "aNrm", "aCol");
        glEnable(DEPTH_TEST); glDepthFunc(LEQUAL); glDepthMask(1); glDisable(BLEND);
        if (clip.HasValue) glEnable(CLIP_DISTANCE0);
        // Where both surfaces are in the same place the first one drawn is the one that shows: the second is set
        // back by a hair, as a decal would be brought forward, so the two do not flicker through each other.
        if (which == 1) { glEnable(POLYGON_OFFSET_FILL); glPolygonOffset(1f, 2f); }
        UseProgram(_overlay);
        Mat(_overlay, "uMV", mv); Mat(_overlay, "uMVP", mvp); Clip(_overlay, clip);
        Uniform4f(Loc(_overlay, "uProj"), proj.M11, proj.M22, proj.M33, proj.M43);
        Uniform1f(Loc(_overlay, "uOrtho"), ortho ? 1 : 0);
        Uniform1f(Loc(_overlay, "uTol"), tolerance);
        Uniform3f(Loc(_overlay, "uOwn"), own.X, own.Y, own.Z);
        Uniform3f(Loc(_overlay, "uShared"), shared.X, shared.Y, shared.Z);
        Uniform4f(Loc(_overlay, "uCut"), cutColor.X, cutColor.Y, cutColor.Z, clip.HasValue ? 1 : 0);
        ActiveTexture(TEXTURE1); glBindTexture(TEXTURE_2D, _ovTex[1 - which]);
        Uniform1i(Loc(_overlay, "uOther"), 1);
        ActiveTexture(TEXTURE2); glBindTexture(TEXTURE_2D, _ovTex[which]);
        Uniform1i(Loc(_overlay, "uSelf"), 2);
        ActiveTexture(TEXTURE0);
        DrawSlot(which);
        glDisable(POLYGON_OFFSET_FILL);
        glDisable(CLIP_DISTANCE0);
        BindVertexArray(0);
    }
}
