// glance-step.exe <input> <output.gmesh> <step|iges|brep>
//
// Native STEP / IGES reader for Glance, built on OpenCascade. Reads the file, tessellates it on all cores
// and writes one packed triangle mesh that Glance loads and caches:
//
//   header: 'GLM1', vertexCount u32, indexCount u32, flags u32 (1 = normals, 2 = colours, 4 = face ids)
//   body:   positions f32[3V], normals f32[3V], colours u8[4V] (when flagged), indices u32[I],
//           face ids u32[I/3] (which CAD face each triangle came from; used for measuring)
//
// Progress lines go to stdout, an error message to stderr; exit code 0 means the mesh was written.

#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include <BRepBndLib.hxx>
#include <BRepLib_ToolTriangulatedShape.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRepTools.hxx>
#include <BRep_Builder.hxx>
#include <BRep_Tool.hxx>
#include <Bnd_Box.hxx>
#include <IGESCAFControl_Reader.hxx>
#include <Message.hxx>
#include <Message_Messenger.hxx>
#include <Message_PrinterOStream.hxx>
#include <NCollection_DataMap.hxx>
#include <NCollection_Sequence.hxx>
#include <Poly_Triangulation.hxx>
#include <Quantity_Color.hxx>
#include <STEPCAFControl_Reader.hxx>
#include <ShapeProcess.hxx>
#include <Standard_Failure.hxx>
#include <TDF_Label.hxx>
#include <TDocStd_Document.hxx>
#include <TopExp_Explorer.hxx>
#include <TopLoc_Location.hxx>
#include <TopTools_ShapeMapHasher.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Compound.hxx>
#include <TopoDS_Face.hxx>
#include <XCAFApp_Application.hxx>
#include <XCAFDoc_ColorTool.hxx>
#include <XCAFDoc_DocumentTool.hxx>
#include <XCAFDoc_ShapeTool.hxx>

namespace
{
LARGE_INTEGER g_t0, g_freq;
bool g_trace = false;

void Trace(const char* what)
{
  if (!g_trace) return;
  LARGE_INTEGER now; QueryPerformanceCounter(&now);
  fprintf(stderr, "trace %6.0f ms  %s\n", (now.QuadPart - g_t0.QuadPart) * 1000.0 / g_freq.QuadPart, what);
  fflush(stderr);
}

void Stage(const char* text)
{
  Trace(text);
  printf("%s\n", text);
  fflush(stdout);
}

struct Mesh
{
  std::vector<float> pos, nrm;
  std::vector<uint32_t> col, idx, face;
  bool anyColor = false;
  uint32_t faces = 0;
};

typedef NCollection_DataMap<TopoDS_Shape, uint32_t, TopTools_ShapeMapHasher> FaceColors;

uint32_t Pack(const Quantity_Color& c)
{
  double r, g, b;
  c.Values(r, g, b, Quantity_TOC_sRGB);
  auto byte = [](double v) { return (uint32_t)(v <= 0 ? 0 : v >= 1 ? 255 : v * 255.0 + 0.5); };
  return byte(r) | byte(g) << 8 | byte(b) << 16 | 0xFF000000u;
}

bool LabelColor(const Handle(XCAFDoc_ColorTool)& colors, const TDF_Label& label, uint32_t& out)
{
  Quantity_Color c;
  if (colors.IsNull()) return false;
  if (colors->GetColor(label, XCAFDoc_ColorSurf, c) || colors->GetColor(label, XCAFDoc_ColorGen, c))
  {
    out = Pack(c);
    return true;
  }
  return false;
}

void EmitFace(const TopoDS_Face& face, bool hasColor, uint32_t color, Mesh& out)
{
  TopLoc_Location loc;
  Handle(Poly_Triangulation) tri = BRep_Tool::Triangulation(face, loc);
  if (tri.IsNull() || tri->NbTriangles() == 0 || tri->NbNodes() == 0) return;
  if (!tri->HasNormals()) BRepLib_ToolTriangulatedShape::ComputeNormals(face, tri);
  if (!tri->HasNormals()) tri->ComputeNormals();

  const gp_Trsf t = loc.Transformation();
  const bool reversed = face.Orientation() == TopAbs_REVERSED;
  const bool flipWinding = reversed != t.IsNegative();
  const uint32_t base = (uint32_t)(out.pos.size() / 3);
  const int n = tri->NbNodes();

  for (int i = 1; i <= n; i++)
  {
    gp_Pnt p = tri->Node(i);
    p.Transform(t);
    out.pos.push_back((float)p.X()); out.pos.push_back((float)p.Y()); out.pos.push_back((float)p.Z());

    gp_Vec v(0, 0, 1);
    if (tri->HasNormals())
    {
      NCollection_Vec3<float> nv;
      tri->Normal(i, nv);
      v = gp_Vec(nv.x(), nv.y(), nv.z());
      v.Transform(t);
      if (reversed) v.Reverse();
      double len = v.Magnitude();
      if (len > 1e-12) v /= len;
    }
    out.nrm.push_back((float)v.X()); out.nrm.push_back((float)v.Y()); out.nrm.push_back((float)v.Z());
    out.col.push_back(hasColor ? color : 0u);
  }
  if (hasColor) out.anyColor = true;

  const int nt = tri->NbTriangles();
  const uint32_t id = out.faces++;
  for (int i = 1; i <= nt; i++)
  {
    int a, b, c;
    tri->Triangle(i).Get(a, b, c);
    if (flipWinding) std::swap(b, c);
    out.idx.push_back(base + a - 1); out.idx.push_back(base + b - 1); out.idx.push_back(base + c - 1);
    out.face.push_back(id);
  }
}

void EmitShape(const TopoDS_Shape& shape, const TopLoc_Location& loc, const FaceColors* faceColors,
               bool hasColor, uint32_t color, Mesh& out)
{
  for (TopExp_Explorer ex(shape, TopAbs_FACE); ex.More(); ex.Next())
  {
    const TopoDS_Shape& f = ex.Current();
    bool fc = hasColor; uint32_t c = color;
    if (faceColors != nullptr && faceColors->IsBound(f)) { fc = true; c = faceColors->Find(f); }
    EmitFace(TopoDS::Face(f.Moved(loc)), fc, c, out);
  }
}

// Walks the assembly tree: references carry placements, simple shapes carry geometry and colours.
void Walk(const Handle(XCAFDoc_ColorTool)& colors, const TDF_Label& label, const TopLoc_Location& loc,
          bool hasColor, uint32_t color, Mesh& out, int depth)
{
  if (depth > 64) return;
  uint32_t own;
  if (LabelColor(colors, label, own)) { hasColor = true; color = own; }

  TDF_Label ref;
  if (XCAFDoc_ShapeTool::IsReference(label) && XCAFDoc_ShapeTool::GetReferredShape(label, ref))
  {
    Walk(colors, ref, loc * XCAFDoc_ShapeTool::GetLocation(label), hasColor, color, out, depth + 1);
    return;
  }
  if (XCAFDoc_ShapeTool::IsAssembly(label))
  {
    NCollection_Sequence<TDF_Label> parts;
    XCAFDoc_ShapeTool::GetComponents(label, parts, false);
    for (int i = 1; i <= parts.Length(); i++) Walk(colors, parts.Value(i), loc, hasColor, color, out, depth + 1);
    return;
  }

  TopoDS_Shape shape;
  if (!XCAFDoc_ShapeTool::GetShape(label, shape) || shape.IsNull()) return;

  FaceColors faceColors;
  NCollection_Sequence<TDF_Label> subs;
  XCAFDoc_ShapeTool::GetSubShapes(label, subs);
  for (int pass = 0; pass < 2; pass++)          // bodies first, then single faces on top
  {
    for (int i = 1; i <= subs.Length(); i++)
    {
      uint32_t sc;
      TopoDS_Shape sub;
      if (!LabelColor(colors, subs.Value(i), sc)) continue;
      if (!XCAFDoc_ShapeTool::GetShape(subs.Value(i), sub) || sub.IsNull()) continue;
      const bool isFace = sub.ShapeType() == TopAbs_FACE;
      if (isFace != (pass == 1)) continue;
      for (TopExp_Explorer ex(sub, TopAbs_FACE); ex.More(); ex.Next())
      {
        if (faceColors.IsBound(ex.Current())) faceColors.ChangeFind(ex.Current()) = sc;
        else faceColors.Bind(ex.Current(), sc);
      }
    }
  }
  EmitShape(shape, loc, faceColors.IsEmpty() ? nullptr : &faceColors, hasColor, color, out);
}

void Tessellate(const TopoDS_Shape& all)
{
  Bnd_Box box;
  BRepBndLib::Add(all, box, false);
  if (box.IsVoid()) throw Standard_Failure("There are no surfaces in this file.");
  double x0, y0, z0, x1, y1, z1;
  box.Get(x0, y0, z0, x1, y1, z1);
  const double size = ((x1 - x0) + (y1 - y0) + (z1 - z0)) / 3.0;
  const double linear = size > 0 ? size * 0.0006 : 0.01;
  BRepMesh_IncrementalMesh mesher(all, linear, false, 0.2, true);
}

std::string Utf8(const wchar_t* w)
{
  int n = WideCharToMultiByte(CP_UTF8, 0, w, -1, nullptr, 0, nullptr, nullptr);
  std::string s(n > 0 ? n - 1 : 0, '\0');
  if (n > 1) WideCharToMultiByte(CP_UTF8, 0, w, -1, &s[0], n, nullptr, nullptr);
  return s;
}

int Run(const wchar_t* input, const wchar_t* output, const std::wstring& format)
{
  const std::string path = Utf8(input);
  Mesh mesh;
  Stage("Reading geometry");

  if (format == L"brep")
  {
    TopoDS_Shape shape;
    BRep_Builder builder;
    if (!BRepTools::Read(shape, path.c_str(), builder) || shape.IsNull())
      throw Standard_Failure("The geometry in this file couldn't be read.");
    Stage("Building surfaces");
    Tessellate(shape);
    EmitShape(shape, TopLoc_Location(), nullptr, false, 0, mesh);
  }
  else
  {
    Handle(TDocStd_Document) doc;
    XCAFApp_Application::GetApplication()->NewDocument("BinXCAF", doc);
    // Shape repair (re-sewing and fixing sloppy geometry) is what a CAD system needs before it edits a model.
    // For looking at one it is most of the load time and changes nothing visible, so it is off unless asked for.
    const bool repair = GetEnvironmentVariableW(L"GLANCE_STEP_REPAIR", nullptr, 0) != 0;
    bool ok;
    if (format == L"iges")
    {
      IGESCAFControl_Reader reader;
      reader.SetColorMode(true); reader.SetNameMode(false); reader.SetLayerMode(false);
      if (!repair) reader.SetShapeProcessFlags(ShapeProcess::OperationsFlags());
      ok = reader.ReadFile(path.c_str()) == IFSelect_RetDone;
      Trace("file parsed");
      ok = ok && reader.Transfer(doc);
    }
    else
    {
      STEPCAFControl_Reader reader;
      reader.SetColorMode(true); reader.SetNameMode(false); reader.SetLayerMode(false);
      reader.SetPropsMode(false); reader.SetGDTMode(false); reader.SetMatMode(false);
      reader.SetViewMode(false); reader.SetMetaMode(false);
      if (!repair) reader.SetShapeProcessFlags(ShapeProcess::OperationsFlags());
      ok = reader.ReadFile(path.c_str()) == IFSelect_RetDone;
      Trace("file parsed");
      ok = ok && reader.Transfer(doc);
    }
    if (!ok) throw Standard_Failure("The geometry in this file couldn't be read.");
    Trace("shapes built");

    Handle(XCAFDoc_ShapeTool) shapes = XCAFDoc_DocumentTool::ShapeTool(doc->Main());
    Handle(XCAFDoc_ColorTool) colors = XCAFDoc_DocumentTool::ColorTool(doc->Main());
    NCollection_Sequence<TDF_Label> roots;
    shapes->GetFreeShapes(roots);

    TopoDS_Compound all;
    BRep_Builder builder;
    builder.MakeCompound(all);
    int count = 0;
    for (int i = 1; i <= roots.Length(); i++)
    {
      TopoDS_Shape s;
      if (XCAFDoc_ShapeTool::GetShape(roots.Value(i), s) && !s.IsNull()) { builder.Add(all, s); count++; }
    }
    if (count == 0) throw Standard_Failure("There are no surfaces in this file.");

    Stage("Building surfaces");
    Tessellate(all);
    Trace("tessellated");
    for (int i = 1; i <= roots.Length(); i++) Walk(colors, roots.Value(i), TopLoc_Location(), false, 0, mesh, 0);
  }

  if (mesh.idx.empty()) throw Standard_Failure("There are no surfaces in this file.");
  Trace("mesh collected");

  FILE* f = _wfopen(output, L"wb");
  if (f == nullptr) throw Standard_Failure("The converted model couldn't be saved.");
  const uint32_t header[4] = { 0x314D4C47u, (uint32_t)(mesh.pos.size() / 3), (uint32_t)mesh.idx.size(),
                               1u | (mesh.anyColor ? 2u : 0u) | 4u };
  bool written = fwrite(header, sizeof(header), 1, f) == 1
    && fwrite(mesh.pos.data(), sizeof(float), mesh.pos.size(), f) == mesh.pos.size()
    && fwrite(mesh.nrm.data(), sizeof(float), mesh.nrm.size(), f) == mesh.nrm.size()
    && (!mesh.anyColor || fwrite(mesh.col.data(), sizeof(uint32_t), mesh.col.size(), f) == mesh.col.size())
    && fwrite(mesh.idx.data(), sizeof(uint32_t), mesh.idx.size(), f) == mesh.idx.size()
    && fwrite(mesh.face.data(), sizeof(uint32_t), mesh.face.size(), f) == mesh.face.size();
  written = (fclose(f) == 0) && written;
  if (!written) { _wremove(output); throw Standard_Failure("The converted model couldn't be saved."); }
  Trace("written");
  return 0;
}
} // namespace

int wmain(int argc, wchar_t** argv)
{
  QueryPerformanceFrequency(&g_freq);
  QueryPerformanceCounter(&g_t0);
  g_trace = GetEnvironmentVariableW(L"GLANCE_STEP_TRACE", nullptr, 0) != 0;
  SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
  if (argc < 3) { fprintf(stderr, "usage: glance-step <input> <output> [step|iges|brep]\n"); return 2; }
  Trace("main");

  // OpenCascade reports its own statistics on stdout; Glance shows stdout lines to the user, so silence it.
  Message::DefaultMessenger()->RemovePrinters(STANDARD_TYPE(Message_PrinterOStream));

  try
  {
    int code = Run(argv[1], argv[2], argc > 3 ? std::wstring(argv[3]) : std::wstring(L"step"));
    fflush(stdout);
    TerminateProcess(GetCurrentProcess(), (UINT)code);   // skip tearing down a large CAD model
    return code;
  }
  catch (const Standard_Failure& e)
  {
    const char* msg = e.what();
    fprintf(stderr, "%s\n", msg != nullptr && msg[0] != '\0' ? msg : "The geometry in this file couldn't be read.");
  }
  catch (const std::bad_alloc&)
  {
    fprintf(stderr, "This model needs more memory than is available.\n");
  }
  catch (...)
  {
    fprintf(stderr, "The geometry in this file couldn't be read.\n");
  }
  fflush(stderr);
  return 3;
}
