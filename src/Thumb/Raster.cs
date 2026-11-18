using System.Numerics;

namespace Glance;

/// <summary>
/// Draws a model the way the viewer first shows it (the same isometric view, lighting and edge lines; see App.cs and
/// the mesh shader in Renderer.cs) on a transparent background, using the processor alone. No graphics driver, no
/// window: a thumbnail is at most a few hundred pixels across, where a small software rasterizer is quick, gives
/// the same picture on every PC, and cannot be upset by whatever graphics hardware or session it runs in.
///
/// The picture is drawn several times larger than asked for and averaged down, which is what keeps it clean at
/// 32 pixels as well as at 256.
/// </summary>
internal static unsafe class Raster
{
    // The viewer's default camera (App.cs: Fov, IsoYaw, IsoPitch) and its material on a light background.
    const float Fov = 0.61f, IsoYaw = -0.96f, IsoPitch = 0.49f, Rim = 0.05f;
    static readonly Vector3 Material = new(0x70 / 255f, 0x80 / 255f, 0x90 / 255f);          // Slate
    static readonly Vector3 EdgeColor = new(0.05f, 0.06f, 0.08f);
    const float EdgeAlpha = 0.62f;
    static readonly Vector3 L1 = Vector3.Normalize(new(0.45f, 0.65f, 0.62f)), L2 = Vector3.Normalize(new(-0.70f, 0.15f, 0.50f));

    /// <summary>Samples along each side of a pixel.</summary>
    public static int Oversampling(int px) => px <= 64 ? 8 : px <= 256 ? 4 : px <= 512 ? 3 : 2;

    /// <summary>
    /// The picture as BGRA bytes, top row first, colour already multiplied by alpha (what Explorer expects).
    /// Its longer side is <paramref name="px"/>; the shorter one is as long as the model needs, no padding.
    /// </summary>
    public static byte[] Draw(Mesh m, int px, out int width, out int height)
    {
        int ss = Oversampling(px);
        float[] pos = m.Pos;
        uint[]? idx = m.Idx;
        int V = m.VertexCount, T = m.TriCount;

        // ── The camera. View space: x to the right, y up, z towards the viewer. ──
        Vector3 min = m.Min, max = m.Max;
        if (m.YUp) (min, max) = (new Vector3(min.X, -max.Z, min.Y), new Vector3(max.X, -min.Z, max.Y));     // world = (x, -z, y)
        Vector3 center = (min + max) / 2, half = (max - min) / 2;
        float radius = half.Length();
        if (!(radius > 0) || float.IsInfinity(radius)) throw new InvalidDataException();

        float cp = MathF.Cos(IsoPitch), sp = MathF.Sin(IsoPitch), cy = MathF.Cos(IsoYaw), sy = MathF.Sin(IsoYaw);
        Vector3 dir = new(cp * cy, cp * sy, sp), right = new(-sy, cy, 0), up = Vector3.Cross(dir, right);
        float t = MathF.Tan(Fov / 2), dist = 0;
        for (int i = 0; i < 8; i++)
        {
            var rel = new Vector3((i & 1) == 0 ? -half.X : half.X, (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
            float z = Vector3.Dot(rel, dir);
            dist = MathF.Max(dist, z + MathF.Max(MathF.Abs(Vector3.Dot(rel, right)), MathF.Abs(Vector3.Dot(rel, up))) / t);
        }
        dist = MathF.Max(dist * 1.22f, radius * 0.01f);
        Vector3 eye = center + dir * dist;

        // The same three axes in the file's own coordinates, so a vertex goes from file to view in one step.
        Vector3 Rm = Model(right, m.YUp), Um = Model(up, m.YUp), Dm = Model(dir, m.YUp);
        float er = Vector3.Dot(eye, right), eu = Vector3.Dot(eye, up), ed = Vector3.Dot(eye, dir);

        // ── Every vertex: where it falls in the view, and how far away. ──
        float[] vs = new float[(long)V * 3];
        float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
        float near = dist * 1e-4f;
        for (int i = 0; i < V; i++)
        {
            var p = new Vector3(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]);
            float depth = ed - Vector3.Dot(p, Dm);
            if (!(depth > near)) depth = near;
            float k = 1 / (depth * t), nx = (Vector3.Dot(p, Rm) - er) * k, ny = (Vector3.Dot(p, Um) - eu) * k;
            vs[i * 3] = nx; vs[i * 3 + 1] = ny; vs[i * 3 + 2] = 1 / depth;
            if (nx < x0) x0 = nx; if (nx > x1) x1 = nx; if (ny < y0) y0 = ny; if (ny > y1) y1 = ny;
        }
        float bw = x1 - x0, bh = y1 - y0, big = MathF.Max(bw, bh);
        if (!(big > 0) || float.IsInfinity(big)) throw new InvalidDataException();

        // ── The frame: the model's own outline, as large as the picture allows, with room for an edge line around it. ──
        float pad = ss * 1.25f, scale = (px * ss - 2 * pad) / big;
        width = Math.Clamp((int)MathF.Ceiling((bw * scale + 2 * pad) / ss), 1, px);
        height = Math.Clamp((int)MathF.Ceiling((bh * scale + 2 * pad) / ss), 1, px);
        int W = width * ss, H = height * ss;
        float mx = (x0 + x1) / 2, my = (y0 + y1) / 2, ox = W / 2f, oy = H / 2f;
        for (int i = 0; i < V; i++)
        {
            vs[i * 3] = (vs[i * 3] - mx) * scale + ox;
            vs[i * 3 + 1] = oy - (vs[i * 3 + 1] - my) * scale;
        }

        // ── Which triangle is nearest at every sample. Whole-number arithmetic, so neighbours never leave a gap. ──
        float[] zb = new float[(long)W * H];            // 1 / depth of the nearest surface; 0 where there is none
        int[] tb = new int[(long)W * H];
        Array.Fill(tb, -1);
        fixed (float* pv = vs, pz = zb)
        fixed (int* pt = tb)
        fixed (uint* pi = idx)
        {
            for (int tri = 0; tri < T; tri++)
            {
                int a, b, c;
                if (pi != null) { a = (int)pi[tri * 3]; b = (int)pi[tri * 3 + 1]; c = (int)pi[tri * 3 + 2]; }
                else { a = tri * 3; b = a + 1; c = a + 2; }
                float* A = pv + (long)a * 3, B = pv + (long)b * 3, C = pv + (long)c * 3;
                int ax = Fix(A[0]), ay = Fix(A[1]), bx = Fix(B[0]), by = Fix(B[1]), cx = Fix(C[0]), cyy = Fix(C[1]);
                float az = A[2], bz = B[2], cz = C[2];
                long area = (long)(bx - ax) * (cyy - ay) - (long)(by - ay) * (cx - ax);
                if (area == 0) continue;
                if (area < 0) { (bx, cx) = (cx, bx); (by, cyy) = (cyy, by); (bz, cz) = (cz, bz); area = -area; }

                int minX = Math.Min(ax, Math.Min(bx, cx)), maxX = Math.Max(ax, Math.Max(bx, cx));
                int minY = Math.Min(ay, Math.Min(by, cyy)), maxY = Math.Max(ay, Math.Max(by, cyy));
                int i0 = Math.Max(0, (minX + 7) >> 4), i1 = Math.Min(W - 1, (maxX - 8) >> 4);
                int j0 = Math.Max(0, (minY + 7) >> 4), j1 = Math.Min(H - 1, (maxY - 8) >> 4);
                if (i0 > i1 || j0 > j1) continue;

                // Edge functions at the first sample centre; a sample exactly on an edge belongs to one side only.
                long sx = i0 * 16 + 8, syy = j0 * 16 + 8;
                long dxAB = bx - ax, dyAB = by - ay, dxBC = cx - bx, dyBC = cyy - by, dxCA = ax - cx, dyCA = ay - cyy;
                long eAB = dxAB * (syy - ay) - dyAB * (sx - ax) - (Inclusive(dxAB, dyAB) ? 0 : 1);
                long eBC = dxBC * (syy - by) - dyBC * (sx - bx) - (Inclusive(dxBC, dyBC) ? 0 : 1);
                long eCA = dxCA * (syy - cyy) - dyCA * (sx - cx) - (Inclusive(dxCA, dyCA) ? 0 : 1);
                // Row by row, only the run of samples that lies inside all three edges is visited: a long thin triangle
                // (CAD files are full of them) costs what it covers, not what its bounding box covers.
                double inv = 1.0 / area;
                long stepAB = -dyAB * 16, stepBC = -dyBC * 16, stepCA = -dyCA * 16;       // one sample to the right
                double zStep = (stepBC * (double)az + stepCA * (double)bz + stepAB * (double)cz) * inv;
                if (i1 - i0 < 6)
                {
                    // Narrow (most triangles of a dense mesh, and upright slivers): quicker to try each sample than to work out the run.
                    for (int j = j0; j <= j1; j++, eAB += dxAB * 16, eBC += dxBC * 16, eCA += dxCA * 16)
                    {
                        long wAB = eAB, wBC = eBC, wCA = eCA;
                        long row = (long)j * W;
                        for (int i = i0; i <= i1; i++, wAB += stepAB, wBC += stepBC, wCA += stepCA)
                        {
                            if ((wAB | wBC | wCA) < 0) continue;
                            float z = (float)((wBC * (double)az + wCA * (double)bz + wAB * (double)cz) * inv);
                            if (z > pz[row + i]) { pz[row + i] = z; pt[row + i] = tri; }
                        }
                    }
                    continue;
                }
                for (int j = j0; j <= j1; j++, eAB += dxAB * 16, eBC += dxBC * 16, eCA += dxCA * 16)
                {
                    int lo = 0, hi = i1 - i0;
                    Clip(eAB, stepAB, ref lo, ref hi); Clip(eBC, stepBC, ref lo, ref hi); Clip(eCA, stepCA, ref lo, ref hi);
                    if (lo > hi) continue;
                    double z = ((eBC + stepBC * lo) * (double)az + (eCA + stepCA * lo) * (double)bz + (eAB + stepAB * lo) * (double)cz) * inv;
                    float* zr = pz + (long)j * W + i0 + lo;
                    int* tr = pt + (long)j * W + i0 + lo;
                    for (int n = hi - lo; n >= 0; n--, z += zStep, zr++, tr++)
                        if ((float)z > *zr) { *zr = (float)z; *tr = tri; }
                }
            }
        }

        // ── Edge lines, where the mesh analysis found any: over the surface they belong to, hidden behind nearer ones. ──
        byte[]? cov = null;
        if (m.EdgeLines is { Length: >= 6 } el && px >= 40)
        {
            cov = new byte[(long)W * H];
            float lineHalf = Math.Clamp(px / 256f, 0.55f, 1.5f) * 1.1f * ss / 2;
            float flat = radius * 0.004f, steepest = radius * 0.06f, steep = 0;
            int steepTri = -1;
            for (int s = 0; s + 5 < el.Length; s += 6)
            {
                if (!Project(el, s, out float ax, out float ay, out float az) || !Project(el, s + 3, out float bx, out float by, out float bz)) continue;
                float dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                bool wide = MathF.Abs(dx) >= MathF.Abs(dy);
                float along = wide ? dx : dy, across = wide ? dy : dx;
                float slope = MathF.Abs(along) > 1e-6f ? across / along : 0;
                float reach = lineHalf * MathF.Sqrt(1 + slope * slope) + 1;
                float a0 = wide ? ax : ay, a1 = wide ? bx : by, c0 = wide ? ay : ax;
                int from = Math.Max(0, (int)MathF.Floor(MathF.Min(a0, a1) - lineHalf)), to = Math.Min((wide ? W : H) - 1, (int)MathF.Ceiling(MathF.Max(a0, a1) + lineHalf));
                for (int u = from; u <= to; u++)
                {
                    float mid = c0 + (Math.Clamp(u + 0.5f, MathF.Min(a0, a1), MathF.Max(a0, a1)) - a0) * slope;
                    int lo = Math.Max(0, (int)MathF.Floor(mid - reach)), hi = Math.Min((wide ? H : W) - 1, (int)MathF.Ceiling(mid + reach));
                    for (int v = lo; v <= hi; v++)
                    {
                        int x = wide ? u : v, y = wide ? v : u;
                        float qx = x + 0.5f - ax, qy = y + 0.5f - ay;
                        float f = len2 > 0 ? Math.Clamp((qx * dx + qy * dy) / len2, 0, 1) : 0;
                        float rx = qx - f * dx, ry = qy - f * dy;
                        if (rx * rx + ry * ry > lineHalf * lineHalf) continue;
                        long at = (long)y * W + x;
                        if (cov[at] != 0) continue;
                        float surface = zb[at];
                        if (surface > 0)
                        {
                            // As far behind the surface as the surface itself recedes across the width of the line, and no further.
                            float lineDepth = 1 / (az + f * (bz - az)), depth = 1 / surface;
                            if (lineDepth > depth + flat)
                            {
                                int tri = tb[at];
                                if (tri != steepTri) { steepTri = tri; steep = Steepness(tri); }
                                if (lineDepth > depth + flat + MathF.Min(steepest, steep * depth * depth * (lineHalf + 1))) continue;
                            }
                        }
                        cov[at] = 1;
                    }
                }
            }
        }

        // ── Light every sample that shows something and average the samples of each pixel. ──
        // Within one pixel a triangle is lit once, at the first of its samples, and the result reused for the others
        // (what a graphics card's multisampling does): the outline stays as smooth, at a fraction of the work.
        float[]? nrm = m.Nrm ?? m.SmoothNrm;
        uint[]? col = Legible(m.Col);
        byte[] image = new byte[(long)width * height * 4];
        float per = 255f / (ss * ss);
        int flatTri = -1; Vector3 flatN = default;
        int* seenTri = stackalloc int[4];
        Vector3* seenLit = stackalloc Vector3[4];
        for (int Y = 0; Y < height; Y++)
        for (int X = 0; X < width; X++)
        {
            float r = 0, g = 0, b = 0, alpha = 0;
            int seen = 0, next = 0;
            for (int j = Y * ss; j < Y * ss + ss; j++)
            for (int i = X * ss; i < X * ss + ss; i++)
            {
                long at = (long)j * W + i;
                int tri = tb[at];
                bool line = cov != null && cov[at] != 0;
                if (tri < 0)
                {
                    if (line) { r += EdgeColor.X * EdgeAlpha; g += EdgeColor.Y * EdgeAlpha; b += EdgeColor.Z * EdgeAlpha; alpha += EdgeAlpha; }
                    continue;
                }
                int hit = 0;
                while (hit < seen && seenTri[hit] != tri) hit++;
                if (hit == seen)
                {
                    if (seen < 4) seen++; else { hit = next; next = (next + 1) & 3; }
                    seenTri[hit] = tri; seenLit[hit] = Shade(tri, i + 0.5f, j + 0.5f);
                }
                Vector3 c = seenLit[hit];
                if (line) c += (EdgeColor - c) * EdgeAlpha;
                r += c.X; g += c.Y; b += c.Z; alpha += 1;
            }
            if (alpha <= 0) continue;
            long o = ((long)Y * width + X) * 4;
            image[o] = (byte)(b * per + 0.5f); image[o + 1] = (byte)(g * per + 0.5f); image[o + 2] = (byte)(r * per + 0.5f);
            image[o + 3] = (byte)(alpha * per + 0.5f);
        }
        return image;

        // ───────────────────────── helpers ─────────────────────────

        bool Project(float[] line, int o, out float x, out float y, out float iz)
        {
            var p = new Vector3(line[o], line[o + 1], line[o + 2]);
            float depth = ed - Vector3.Dot(p, Dm);
            x = y = iz = 0;
            if (!(depth > near)) return false;
            float k = 1 / (depth * t);
            x = ((Vector3.Dot(p, Rm) - er) * k - mx) * scale + ox;
            y = oy - ((Vector3.Dot(p, Um) - eu) * k - my) * scale;
            iz = 1 / depth;
            return !float.IsNaN(x + y);
        }

        void Corners(int tri, out int a, out int b, out int c)
        {
            if (idx != null) { a = (int)idx[tri * 3]; b = (int)idx[tri * 3 + 1]; c = (int)idx[tri * 3 + 2]; }
            else { a = tri * 3; b = a + 1; c = a + 2; }
        }

        // How fast 1/depth changes across a triangle, per sample.
        float Steepness(int tri)
        {
            Corners(tri, out int a, out int b, out int c);
            float ux = vs[b * 3] - vs[a * 3], uy = vs[b * 3 + 1] - vs[a * 3 + 1], uz = vs[b * 3 + 2] - vs[a * 3 + 2];
            float wx = vs[c * 3] - vs[a * 3], wy = vs[c * 3 + 1] - vs[a * 3 + 1], wz = vs[c * 3 + 2] - vs[a * 3 + 2];
            float nz = ux * wy - uy * wx;
            if (MathF.Abs(nz) < 1e-12f) return float.MaxValue;
            float gx = (uy * wz - uz * wy) / nz, gy = (uz * wx - ux * wz) / nz;
            return MathF.Sqrt(gx * gx + gy * gy);
        }

        // The viewer's mesh shader (Renderer.cs, the plain shaded style), one sample at a time.
        Vector3 Shade(int tri, float sxp, float syp)
        {
            Corners(tri, out int a, out int b, out int c);
            float ax = vs[a * 3], ay = vs[a * 3 + 1], bx = vs[b * 3], by = vs[b * 3 + 1], cx = vs[c * 3], cyy = vs[c * 3 + 1];
            float area = (bx - ax) * (cyy - ay) - (cx - ax) * (by - ay);
            float wa = 1f / 3, wb = 1f / 3, wc = 1f / 3;
            if (MathF.Abs(area) > 1e-12f)
            {
                wa = ((bx - sxp) * (cyy - syp) - (cx - sxp) * (by - syp)) / area;
                wb = ((cx - sxp) * (ay - syp) - (ax - sxp) * (cyy - syp)) / area;
                wc = 1 - wa - wb;
                // Weights measured on the screen, corrected for perspective.
                wa *= vs[a * 3 + 2]; wb *= vs[b * 3 + 2]; wc *= vs[c * 3 + 2];
                float sum = wa + wb + wc;
                if (MathF.Abs(sum) > 1e-20f) { wa /= sum; wb /= sum; wc /= sum; } else wa = wb = wc = 1f / 3;
            }

            Vector3 n = default;
            if (nrm != null)
            {
                var mn = new Vector3(nrm[a * 3], nrm[a * 3 + 1], nrm[a * 3 + 2]) * wa + new Vector3(nrm[b * 3], nrm[b * 3 + 1], nrm[b * 3 + 2]) * wb
                    + new Vector3(nrm[c * 3], nrm[c * 3 + 1], nrm[c * 3 + 2]) * wc;
                n = new Vector3(Vector3.Dot(mn, Rm), Vector3.Dot(mn, Um), Vector3.Dot(mn, Dm));
            }
            if (!(n.LengthSquared() > 1e-12f))
            {
                if (tri != flatTri)
                {
                    var pa = new Vector3(pos[a * 3], pos[a * 3 + 1], pos[a * 3 + 2]);
                    var fn = Vector3.Cross(new Vector3(pos[b * 3], pos[b * 3 + 1], pos[b * 3 + 2]) - pa, new Vector3(pos[c * 3], pos[c * 3 + 1], pos[c * 3 + 2]) - pa);
                    flatN = new Vector3(Vector3.Dot(fn, Rm), Vector3.Dot(fn, Um), Vector3.Dot(fn, Dm));
                    flatTri = tri;
                }
                n = flatN;
                if (!(n.LengthSquared() > 0)) n = Vector3.UnitZ;
            }
            n = Vector3.Normalize(n);

            var view = Vector3.Normalize(new Vector3(-((sxp - ox) / scale + mx) * t, -((oy - syp) / scale + my) * t, 1));
            if (Vector3.Dot(n, view) < 0) n = -n;

            Vector3 basis = Material;
            if (col != null)
            {
                Vector4 k = Rgba(col[a]) * wa + Rgba(col[b]) * wb + Rgba(col[c]) * wc;
                basis += (new Vector3(k.X, k.Y, k.Z) - basis) * Math.Clamp(k.W, 0, 1);
            }
            float key = MathF.Max(Vector3.Dot(n, L1), 0), fill = MathF.Max(Vector3.Dot(n, L2), 0), sky = n.Y * 0.5f + 0.5f;
            float s = MathF.Max(Vector3.Dot(n, Vector3.Normalize(L1 + view)), 0);
            float s8 = s * s; s8 *= s8; s8 *= s8;                       // s^8
            float s16 = s8 * s8, s32 = s16 * s16;
            float spec = s32 * s16 * s8 * 0.16f;                         // s^56
            float f = 1 - MathF.Max(Vector3.Dot(n, view), 0);
            float fres = f * f * f * f * Rim;
            Vector3 lit = basis * (0.40f + 0.16f * sky + 0.52f * key + 0.14f * fill) + new Vector3(spec + fres);
            return Vector3.Clamp(lit, Vector3.Zero, Vector3.One);
        }
    }

    /// <summary>
    /// The file's own colours, unless every one of them is black or nearly so. Some files (IGES above all) carry
    /// "no colour" as black; the viewer shows that as it is and lets the user switch to a material, but a thumbnail
    /// cannot be switched, and a black shape shows nothing of the part. Such a model gets the default material.
    /// </summary>
    static uint[]? Legible(uint[]? col)
    {
        if (col == null) return null;
        foreach (uint c in col)
            if (c >> 24 != 0 && ((c & 255) > 44 || ((c >> 8) & 255) > 44 || ((c >> 16) & 255) > 44)) return col;
        return null;
    }

    static Vector3 Model(Vector3 world, bool yUp) => yUp ? new Vector3(world.X, world.Z, -world.Y) : world;

    static Vector4 Rgba(uint c) => new Vector4(c & 255, (c >> 8) & 255, (c >> 16) & 255, c >> 24) * (1f / 255);

    /// <summary>Sample coordinates in sixteenths, the grid the triangle edges are measured on.</summary>
    static int Fix(float v) => (int)MathF.Round(Math.Clamp(v, -1e6f, 1e6f) * 16);

    static bool Inclusive(long dx, long dy) => dy > 0 || (dy == 0 && dx < 0);

    /// <summary>
    /// Narrows lo..hi to the steps k for which an edge function stays at or above zero: it is <paramref name="e"/> at k = 0
    /// and grows by <paramref name="step"/> with each k. Leaves lo above hi when there are none.
    /// </summary>
    static void Clip(long e, long step, ref int lo, ref int hi)
    {
        if (step > 0)
        {
            if (e < 0) { long first = (-e + step - 1) / step; if (first > lo) lo = first > int.MaxValue ? int.MaxValue : (int)first; }
        }
        else if (e < 0) lo = int.MaxValue;
        else if (step < 0) { long last = e / -step; if (last < hi) hi = (int)last; }
    }
}
