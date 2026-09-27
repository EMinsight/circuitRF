// occt_probe -- brief-em3d-61, the kernel spike. SPIKE MATERIAL, NOT PRODUCT CODE.
//
// One sub-command per spike question; each writes its evidence under <out>/<q>/ and prints a
// one-line verdict. Brief 62 writes the geometry worker; it may read this file, but nothing under
// src/ or tools/ may include, copy or build it.
//
// Lengths are micrometres throughout (brief 61 section 2): OCCT's tolerances are absolute
// (Precision::Confusion() = 1e-7), which sits nine orders of magnitude below a micrometre-scale
// feature and two below one in metres.
//
// Usage: occt_probe <selftest|q4|q5|q6|q7|q8|q9 <case>|q10|q12|noop> [--out <dir>]

#include <BOPAlgo_Alerts.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepAlgoAPI_Fuse.hxx>
#include <BRepBndLib.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepFilletAPI_MakeChamfer.hxx>
#include <BRepFilletAPI_MakeFillet.hxx>
#include <BRepGProp.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRepPrimAPI_MakePrism.hxx>
#include <BRepTools.hxx>
#include <BRep_Builder.hxx>
#include <BRep_Tool.hxx>
#include <Bnd_Box.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <GProp_GProps.hxx>
#include <Message.hxx>
#include <Message_Messenger.hxx>
#include <Message_PrinterOStream.hxx>
#include <Message_ProgressIndicator.hxx>
#include <Message_ProgressScope.hxx>
#include <OSD.hxx>
#include <Poly_PolygonOnTriangulation.hxx>
#include <Poly_Triangulation.hxx>
#include <Quantity_Color.hxx>
#include <STEPCAFControl_Reader.hxx>
#include <STEPCAFControl_Writer.hxx>
#include <STEPControl_Reader.hxx>
#include <DESTEP_Parameters.hxx>
#include <ShapeFix_Shape.hxx>
#include <ShapeExtend_Status.hxx>
#include <Standard_ErrorHandler.hxx>
#include <Standard_Failure.hxx>
#include <Standard_Version.hxx>
#include <TDataStd_Name.hxx>
#include <TDF_ChildIterator.hxx>
#include <TDF_Label.hxx>
#include <TDocStd_Document.hxx>
#include <TopExp.hxx>
#include <TopExp_Explorer.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Compound.hxx>
#include <XCAFApp_Application.hxx>
#include <XCAFDoc_ColorTool.hxx>
#include <XCAFDoc_DocumentTool.hxx>
#include <XCAFDoc_ShapeTool.hxx>
#include <gp_Ax2.hxx>
#include <gp_Trsf.hxx>

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <map>
#include <numeric>
#include <set>
#include <sstream>
#include <string>
#include <vector>

using Shapes    = NCollection_IndexedMap<TopoDS_Shape, TopTools_ShapeMapHasher>;
using Ancestors = NCollection_IndexedDataMap<TopoDS_Shape, NCollection_List<TopoDS_Shape>, TopTools_ShapeMapHasher>;
using Clock     = std::chrono::steady_clock;

static std::string g_out = ".";

// ------------------------------------------------------------------------------------------------
// small utilities
// ------------------------------------------------------------------------------------------------

static std::string Dir(const std::string& q)
{
  std::filesystem::create_directories(g_out + "/" + q);
  return g_out + "/" + q + "/";
}

static std::string F(double v, int digits = 10)
{
  char b[64];
  std::snprintf(b, sizeof b, "%.*g", digits, v);
  return b;
}

static double Ms(Clock::time_point t0) { return std::chrono::duration<double, std::milli>(Clock::now() - t0).count(); }

static void WriteFile(const std::string& path, const std::string& text)
{
  std::ofstream f(path, std::ios::binary);
  f << text;
}

static const char* Rid()
{
#if defined(__APPLE__)
  #if defined(__aarch64__)
  return "osx-arm64";
  #else
  return "osx-x64";
  #endif
#elif defined(_WIN32)
  #if defined(_M_ARM64)
  return "win-arm64";
  #elif defined(_M_X64)
  return "win-x64";
  #else
  return "win-x86";
  #endif
#else
  #if defined(__aarch64__)
  return "linux-arm64";
  #else
  return "linux-x64";
  #endif
#endif
}

// SHA-256 (FIPS 180-4), so a byte-identity claim can be checked with any sha256 tool.
static std::string Sha256(const std::string& data)
{
  static const uint32_t k[64] = {
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2};
  uint32_t h[8] = {0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
  std::string m = data;
  uint64_t bits = uint64_t(data.size()) * 8;
  m.push_back(char(0x80));
  while (m.size() % 64 != 56) m.push_back(0);
  for (int i = 7; i >= 0; --i) m.push_back(char((bits >> (i * 8)) & 0xff));
  auto rotr = [](uint32_t x, int n) { return (x >> n) | (x << (32 - n)); };
  for (size_t c = 0; c < m.size(); c += 64)
  {
    uint32_t w[64];
    for (int i = 0; i < 16; ++i)
      w[i] = (uint32_t(uint8_t(m[c + 4 * i])) << 24) | (uint32_t(uint8_t(m[c + 4 * i + 1])) << 16)
           | (uint32_t(uint8_t(m[c + 4 * i + 2])) << 8) | uint32_t(uint8_t(m[c + 4 * i + 3]));
    for (int i = 16; i < 64; ++i)
    {
      uint32_t s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
      uint32_t s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
      w[i]        = w[i - 16] + s0 + w[i - 7] + s1;
    }
    uint32_t a = h[0], b = h[1], cc = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
    for (int i = 0; i < 64; ++i)
    {
      uint32_t S1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25);
      uint32_t ch = (e & f) ^ (~e & g);
      uint32_t t1 = hh + S1 + ch + k[i] + w[i];
      uint32_t S0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22);
      uint32_t mj = (a & b) ^ (a & cc) ^ (b & cc);
      uint32_t t2 = S0 + mj;
      hh = g; g = f; f = e; e = d + t1; d = cc; cc = b; b = a; a = t1 + t2;
    }
    h[0] += a; h[1] += b; h[2] += cc; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
  }
  char out[65];
  for (int i = 0; i < 8; ++i) std::snprintf(out + 8 * i, 9, "%08x", h[i]);
  return out;
}

struct Counts
{
  int solids = 0, shells = 0, faces = 0, edges = 0, vertices = 0;
  std::string Str() const
  {
    return std::to_string(solids) + "/" + std::to_string(shells) + "/" + std::to_string(faces) + "/"
         + std::to_string(edges) + "/" + std::to_string(vertices);
  }
};

static Counts Count(const TopoDS_Shape& s)
{
  Counts c;
  if (s.IsNull()) return c;
  Shapes m;
  TopExp::MapShapes(s, TopAbs_SOLID, m);  c.solids = m.Extent();  m.Clear();
  TopExp::MapShapes(s, TopAbs_SHELL, m);  c.shells = m.Extent();  m.Clear();
  TopExp::MapShapes(s, TopAbs_FACE, m);   c.faces = m.Extent();   m.Clear();
  TopExp::MapShapes(s, TopAbs_EDGE, m);   c.edges = m.Extent();   m.Clear();
  TopExp::MapShapes(s, TopAbs_VERTEX, m); c.vertices = m.Extent();
  return c;
}

static double Volume(const TopoDS_Shape& s)
{
  if (s.IsNull()) return 0;
  GProp_GProps p;
  BRepGProp::VolumeProperties(s, p);
  return p.Mass();
}

static double Area(const TopoDS_Shape& s)
{
  GProp_GProps p;
  BRepGProp::SurfaceProperties(s, p);
  return p.Mass();
}

static bool Valid(const TopoDS_Shape& s) { return !s.IsNull() && BRepCheck_Analyzer(s).IsValid(); }

static double MinFaceArea(const TopoDS_Shape& s)
{
  double a = 1e300;
  for (TopExp_Explorer x(s, TopAbs_FACE); x.More(); x.Next()) a = std::min(a, Area(x.Current()));
  return a;
}

static int Slivers(const TopoDS_Shape& s, double threshold)
{
  int n = 0;
  for (TopExp_Explorer x(s, TopAbs_FACE); x.More(); x.Next())
    if (Area(x.Current()) < threshold) ++n;
  return n;
}

struct Box6 { double x0, y0, z0, x1, y1, z1; };

static Box6 Tight(const TopoDS_Shape& s)
{
  Bnd_Box b;
  BRepBndLib::AddOptimal(s, b, false, false);
  Box6 r;
  b.Get(r.x0, r.y0, r.z0, r.x1, r.y1, r.z1);
  return r;
}

static std::string BrepText(const TopoDS_Shape& s, TopTools_FormatVersion v = TopTools_FormatVersion_VERSION_3)
{
  std::ostringstream o;
  BRepTools::Write(s, o, false, false, v);
  return o.str();
}

static const char* SurfKind(const TopoDS_Face& f)
{
  switch (BRepAdaptor_Surface(f).GetType())
  {
    case GeomAbs_Plane: return "plane";
    case GeomAbs_Cylinder: return "cylinder";
    case GeomAbs_Cone: return "cone";
    case GeomAbs_Sphere: return "sphere";
    case GeomAbs_Torus: return "torus";
    default: return "free-form";
  }
}

// ------------------------------------------------------------------------------------------------
// primitives, in micrometres, with the managed kernel's face names (C3dDocument.FaceNameList)
// ------------------------------------------------------------------------------------------------

struct Named
{
  TopoDS_Shape shape;
  std::vector<std::pair<TopoDS_Shape, std::string>> faces;
};

static TopoDS_Shape MakeBox(double x0, double y0, double z0, double x1, double y1, double z1)
{
  return BRepPrimAPI_MakeBox(gp_Pnt(x0, y0, z0), gp_Pnt(x1, y1, z1)).Shape();
}

static Named NameBox(const TopoDS_Shape& s)
{
  Named n{s, {}};
  Box6 all = Tight(s);
  for (TopExp_Explorer x(s, TopAbs_FACE); x.More(); x.Next())
  {
    Box6 b = Tight(x.Current());
    std::string nm = "?";
    const double e = 1e-9;
    if (std::abs(b.x1 - b.x0) < e) nm = std::abs(b.x0 - all.x0) < e ? "xmin" : "xmax";
    else if (std::abs(b.y1 - b.y0) < e) nm = std::abs(b.y0 - all.y0) < e ? "ymin" : "ymax";
    else if (std::abs(b.z1 - b.z0) < e) nm = std::abs(b.z0 - all.z0) < e ? "zmin" : "zmax";
    n.faces.push_back({x.Current(), nm});
  }
  return n;
}

static TopoDS_Shape MakeCyl(double cx, double cy, double z0, double r, double h)
{
  return BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(cx, cy, z0), gp_Dir(0, 0, 1)), r, h).Shape();
}

static Named NameCyl(const TopoDS_Shape& s)
{
  // top = the cap at the far end of the cylinder's own axis (it need not be z)
  Named n{s, {}};
  gp_Ax1 axis;
  for (TopExp_Explorer x(s, TopAbs_FACE); x.More(); x.Next())
  {
    BRepAdaptor_Surface a(TopoDS::Face(x.Current()));
    if (a.GetType() == GeomAbs_Cylinder) axis = a.Cylinder().Axis();
  }
  for (TopExp_Explorer x(s, TopAbs_FACE); x.More(); x.Next())
  {
    const TopoDS_Face& f = TopoDS::Face(x.Current());
    std::string nm = "side";
    if (BRepAdaptor_Surface(f).GetType() != GeomAbs_Cylinder)
    {
      GProp_GProps p;
      BRepGProp::SurfaceProperties(f, p);
      nm = gp_Vec(axis.Location(), p.CentreOfMass()).Dot(gp_Vec(axis.Direction())) > 1e-9 ? "top" : "bottom";
    }
    n.faces.push_back({f, nm});
  }
  return n;
}

// ------------------------------------------------------------------------------------------------
// naming through history (overview section 1g)
// ------------------------------------------------------------------------------------------------

// A deterministic, geometry-based order for the pieces of a split face: by tight box, lexicographic.
static bool GeoLess(const TopoDS_Shape& a, const TopoDS_Shape& b)
{
  Box6 p = Tight(a), q = Tight(b);
  std::array<double, 6> u{p.x0, p.y0, p.z0, p.x1, p.y1, p.z1}, v{q.x0, q.y0, q.z0, q.x1, q.y1, q.z1};
  for (int i = 0; i < 6; ++i)
    if (std::abs(u[i] - v[i]) > 1e-6) return u[i] < v[i];
  return false;
}

struct NamedResult
{
  TopoDS_Shape shape;
  std::vector<std::vector<std::string>> names; // per result face, index = face map index - 1
  Shapes faceMap;
  std::string table;                           // the history map, human-readable
  int unnamed = 0, multiNamed = 0;

  std::string NameOf(const TopoDS_Shape& f) const
  {
    int i = faceMap.FindIndex(f);
    if (i == 0 || names[i - 1].empty()) return "<unnamed>";
    std::string s = names[i - 1][0];
    for (size_t k = 1; k < names[i - 1].size(); ++k) s += "+" + names[i - 1][k];
    return s;
  }
};

static void Finish(NamedResult& r)
{
  r.unnamed = r.multiNamed = 0;
  for (auto& v : r.names)
  {
    if (v.empty()) ++r.unnamed;
    if (v.size() > 1) ++r.multiNamed;
  }
}

// Name the faces of a history-carrying operation's result from its operands' named faces.
static NamedResult NameThrough(BRepBuilderAPI_MakeShape& op, const TopoDS_Shape& result,
                               const std::vector<std::pair<std::string, const NamedResult*>>& operands)
{
  NamedResult r;
  r.shape = result;
  TopExp::MapShapes(result, TopAbs_FACE, r.faceMap);
  r.names.assign(r.faceMap.Extent(), {});
  std::ostringstream t;
  for (auto& [prefix, nr] : operands)
  {
    for (int i = 1; i <= nr->faceMap.Extent(); ++i)
    {
      const TopoDS_Shape& f  = nr->faceMap(i);
      std::string          nm = prefix.empty() ? nr->NameOf(f) : prefix + ":" + nr->NameOf(f);
      bool                 del = op.IsDeleted(f);
      std::vector<TopoDS_Shape> images;
      for (const TopoDS_Shape& m : op.Modified(f))
        if (r.faceMap.Contains(m)) images.push_back(m);
      int gen = op.Generated(f).Extent();
      std::string state;
      if (del) state = "deleted";
      else if (images.empty() && r.faceMap.Contains(f)) { images.push_back(f); state = "unchanged"; }
      else if (images.empty()) state = "not in result, not deleted";
      else state = "modified into " + std::to_string(images.size());
      std::vector<TopoDS_Shape> ordered = images;
      std::stable_sort(ordered.begin(), ordered.end(), GeoLess);
      bool kernelOrderIsGeoOrder = true;
      for (size_t k = 0; k < images.size(); ++k)
        if (!images[k].IsSame(ordered[k])) kernelOrderIsGeoOrder = false;
      t << "  " << (nm.size() < 20 ? nm + std::string(20 - nm.size(), ' ') : nm) << " " << state;
      if (gen) t << ", generated " << gen;
      if (images.size() > 1) t << (kernelOrderIsGeoOrder ? " (Modified order = geometric order)" : " (Modified order != geometric order)");
      t << " ->";
      for (size_t k = 0; k < ordered.size(); ++k)
      {
        int         idx  = r.faceMap.FindIndex(ordered[k]);
        std::string name = ordered.size() > 1 ? nm + "#" + std::to_string(k + 1) : nm;
        r.names[idx - 1].push_back(name);
        t << " F" << idx << "=" << name;
      }
      t << "\n";
    }
  }
  r.table = t.str();
  Finish(r);
  return r;
}

static NamedResult FromNamed(const Named& n)
{
  NamedResult r;
  r.shape = n.shape;
  TopExp::MapShapes(n.shape, TopAbs_FACE, r.faceMap);
  r.names.assign(r.faceMap.Extent(), {});
  for (auto& [f, nm] : n.faces) r.names[r.faceMap.FindIndex(f) - 1].push_back(nm);
  Finish(r);
  return r;
}

struct EdgeNaming
{
  std::vector<std::pair<TopoDS_Edge, std::string>> edges;
  int seams = 0, degenerate = 0, samePairGroups = 0, samePairEdges = 0;
  std::string table;
};

// Edges are named by the two faces they separate, sorted, joined by '|'; a third '|n' field where a pair repeats.
static EdgeNaming NameEdges(const NamedResult& r)
{
  EdgeNaming en;
  Ancestors anc;
  TopExp::MapShapesAndAncestors(r.shape, TopAbs_EDGE, TopAbs_FACE, anc);
  std::map<std::string, std::vector<TopoDS_Edge>> byPair;
  for (int i = 1; i <= anc.Extent(); ++i)
  {
    TopoDS_Edge e = TopoDS::Edge(anc.FindKey(i));
    if (BRep_Tool::Degenerated(e)) { ++en.degenerate; continue; }
    std::vector<std::string> fn;
    Shapes distinct;
    for (const TopoDS_Shape& f : anc(i)) distinct.Add(f);
    for (int k = 1; k <= distinct.Extent(); ++k) fn.push_back(r.NameOf(distinct(k)));
    if (distinct.Extent() == 1) { fn.push_back(fn[0]); ++en.seams; }
    std::sort(fn.begin(), fn.end());
    std::string key = fn[0];
    for (size_t k = 1; k < fn.size(); ++k) key += "|" + fn[k];
    byPair[key].push_back(e);
  }
  std::ostringstream t;
  for (auto& [key, list] : byPair)
  {
    std::vector<TopoDS_Edge> ordered = list;
    std::stable_sort(ordered.begin(), ordered.end(), [](const TopoDS_Edge& a, const TopoDS_Edge& b) { return GeoLess(a, b); });
    if (ordered.size() > 1) { ++en.samePairGroups; en.samePairEdges += int(ordered.size()); }
    for (size_t k = 0; k < ordered.size(); ++k)
    {
      // a repeated pair takes a third '|' field (overview section 1g as corrected by this spike), not '#n',
      // which a split face's piece already uses
      std::string nm = ordered.size() > 1 ? key + "|" + std::to_string(k + 1) : key;
      en.edges.push_back({ordered[k], nm});
      t << "  " << nm << "\n";
    }
  }
  en.table = t.str();
  return en;
}

static TopoDS_Edge EdgeNamed(const EdgeNaming& en, const std::string& nm)
{
  for (auto& [e, n] : en.edges)
    if (n == nm) return e;
  return TopoDS_Edge();
}

// Fillets and chamfers: carried faces through Modified; new faces from Generated(edge|vertex).
static NamedResult NameBlend(BRepFilletAPI_LocalOperation& op, BRepBuilderAPI_MakeShape& mk, const TopoDS_Shape& result,
                             const NamedResult& target, const EdgeNaming& targetEdges,
                             const std::vector<std::string>& blendedEdges, const char* kind)
{
  NamedResult r = NameThrough(mk, result, {{"", &target}});
  std::ostringstream t;
  t << r.table;
  for (const std::string& en : blendedEdges)
  {
    TopoDS_Edge e = EdgeNamed(targetEdges, en);
    const NCollection_List<TopoDS_Shape>& g = mk.Generated(e);
    std::vector<TopoDS_Shape> faces;
    for (const TopoDS_Shape& s : g)
      if (s.ShapeType() == TopAbs_FACE && r.faceMap.Contains(s)) faces.push_back(s);
    std::stable_sort(faces.begin(), faces.end(), GeoLess);
    t << "  edge " << en << ": Generated " << g.Extent() << " shape(s), " << faces.size() << " result face(s) ->";
    for (size_t k = 0; k < faces.size(); ++k)
    {
      std::string nm = std::string(kind) + "(" + en + ")" + (faces.size() > 1 ? "#" + std::to_string(k + 1) : "");
      r.names[r.faceMap.FindIndex(faces[k]) - 1].push_back(nm);
      t << " F" << r.faceMap.FindIndex(faces[k]) << "=" << nm;
    }
    t << "\n";
  }
  // vertices: corner patches
  Shapes verts;
  TopExp::MapShapes(target.shape, TopAbs_VERTEX, verts);
  for (int i = 1; i <= verts.Extent(); ++i)
  {
    const NCollection_List<TopoDS_Shape>& g = mk.Generated(verts(i));
    int nf = 0;
    for (const TopoDS_Shape& s : g)
      if (s.ShapeType() == TopAbs_FACE && r.faceMap.Contains(s))
      {
        ++nf;
        gp_Pnt p = BRep_Tool::Pnt(TopoDS::Vertex(verts(i)));
        std::string nm = std::string(kind) + "-corner(" + F(p.X(), 6) + "," + F(p.Y(), 6) + "," + F(p.Z(), 6) + ")";
        r.names[r.faceMap.FindIndex(s) - 1].push_back(nm);
      }
    if (nf) t << "  vertex V" << i << ": Generated " << nf << " face(s) (corner patch)\n";
  }
  (void)op;
  r.table = t.str();
  Finish(r);
  return r;
}

static std::string DescribeFaces(const NamedResult& r)
{
  std::ostringstream t;
  for (int i = 1; i <= r.faceMap.Extent(); ++i)
  {
    Box6 b = Tight(r.faceMap(i));
    t << "  F" << i << " " << SurfKind(TopoDS::Face(r.faceMap(i))) << " " << r.NameOf(r.faceMap(i)) << "  box ["
      << F(b.x0, 7) << "," << F(b.y0, 7) << "," << F(b.z0, 7) << " .. " << F(b.x1, 7) << "," << F(b.y1, 7) << ","
      << F(b.z1, 7) << "]\n";
  }
  return t.str();
}

// ------------------------------------------------------------------------------------------------
// the shapes the questions share
// ------------------------------------------------------------------------------------------------

// Q4 / Q10 / Q12 part: a 1000 x 800 x 500 um lid, a r = 150 um bore through it, the bore's top rim filleted.
static TopoDS_Shape BoredLid(double filletR, double defl = 0)
{
  TopoDS_Shape lid  = MakeBox(0, 0, 0, 1000, 800, 500);
  TopoDS_Shape bore = MakeCyl(500, 400, -100, 150, 700);
  BRepAlgoAPI_Cut cut(lid, bore);
  TopoDS_Shape s = cut.Shape();
  if (filletR > 0)
  {
    BRepFilletAPI_MakeFillet mf(s);
    for (TopExp_Explorer x(s, TopAbs_EDGE); x.More(); x.Next())
    {
      Box6 b = Tight(x.Current());
      if (std::abs(b.z0 - 500) < 1e-6 && std::abs(b.z1 - 500) < 1e-6 && b.x0 > 1 && b.x1 < 999) mf.Add(filletR, TopoDS::Edge(x.Current()));
    }
    mf.Build();
    s = mf.Shape();
  }
  (void)defl;
  return s;
}

// ------------------------------------------------------------------------------------------------
// tessellation, merged topologically (edge polygons) and by exact coordinate
// ------------------------------------------------------------------------------------------------

struct Mesh
{
  std::vector<std::array<double, 3>> nodes; // per-face nodes, concatenated
  std::vector<std::array<int, 3>>    tris;
  std::vector<int>                   merged; // node -> representative (topological)
  int mergedCount = 0, exactCount = 0;
  int openTopo = 0, nonManifoldTopo = 0, openExact = 0, nonManifoldExact = 0, degenerateTopo = 0;
};

static int Find(std::vector<int>& p, int i)
{
  while (p[i] != i) { p[i] = p[p[i]]; i = p[i]; }
  return i;
}

static void EdgeUse(const std::vector<std::array<int, 3>>& tris, const std::vector<int>& rep, int& open, int& nonManifold, int* degenerate)
{
  std::map<std::pair<int, int>, int> use;
  int deg = 0;
  for (auto& t : tris)
  {
    int a = rep[t[0]], b = rep[t[1]], c = rep[t[2]];
    if (a == b || b == c || a == c) { ++deg; continue; }
    for (auto [u, v] : {std::pair{a, b}, std::pair{b, c}, std::pair{c, a}}) use[{std::min(u, v), std::max(u, v)}]++;
  }
  open = nonManifold = 0;
  for (auto& [k, n] : use)
  {
    if (n == 1) ++open;
    if (n > 2) ++nonManifold;
  }
  if (degenerate) *degenerate = deg;
}

static Mesh Tessellate(const TopoDS_Shape& s, double linDefl, double angDefl, bool relative)
{
  BRepTools::Clean(s);
  BRepMesh_IncrementalMesh(s, linDefl, relative, angDefl, true);
  Mesh m;
  Shapes faces;
  TopExp::MapShapes(s, TopAbs_FACE, faces);
  std::vector<int> offset(faces.Extent() + 1, 0);
  // edge TShape -> list of node-index arrays (global), one per oriented occurrence in a face
  std::map<const void*, std::vector<std::vector<int>>> polys;
  for (int fi = 1; fi <= faces.Extent(); ++fi)
  {
    TopoDS_Face     f = TopoDS::Face(faces(fi));
    TopLoc_Location loc;
    occ::handle<Poly_Triangulation> tri = BRep_Tool::Triangulation(f, loc);
    offset[fi - 1] = int(m.nodes.size());
    if (tri.IsNull()) continue;
    gp_Trsf tr = loc.Transformation();
    for (int i = 1; i <= tri->NbNodes(); ++i)
    {
      gp_Pnt p = tri->Node(i).Transformed(tr);
      m.nodes.push_back({p.X(), p.Y(), p.Z()});
    }
    bool rev = f.Orientation() == TopAbs_REVERSED;
    for (int i = 1; i <= tri->NbTriangles(); ++i)
    {
      int a, b, c;
      tri->Triangle(i).Get(a, b, c);
      if (rev) std::swap(b, c);
      int o = offset[fi - 1] - 1;
      m.tris.push_back({o + a, o + b, o + c});
    }
    for (TopExp_Explorer x(f, TopAbs_EDGE); x.More(); x.Next())
    {
      TopoDS_Edge e = TopoDS::Edge(x.Current());
      if (BRep_Tool::Degenerated(e)) continue;
      occ::handle<Poly_PolygonOnTriangulation> pol = BRep_Tool::PolygonOnTriangulation(e, tri, loc);
      if (pol.IsNull()) continue;
      std::vector<int> idx;
      for (int k = 1; k <= pol->NbNodes(); ++k) idx.push_back(offset[fi - 1] - 1 + pol->Node(k));
      polys[e.TShape().get()].push_back(idx);
    }
  }
  int n = int(m.nodes.size());
  std::vector<int> p(n);
  std::iota(p.begin(), p.end(), 0);
  for (auto& [key, lists] : polys)
    for (size_t j = 1; j < lists.size(); ++j)
      if (lists[j].size() == lists[0].size())
        for (size_t k = 0; k < lists[0].size(); ++k)
        {
          int a = Find(p, lists[0][k]), b = Find(p, lists[j][k]);
          if (a != b) p[b] = a;
        }
  m.merged.resize(n);
  std::set<int> reps;
  for (int i = 0; i < n; ++i) { m.merged[i] = Find(p, i); reps.insert(m.merged[i]); }
  m.mergedCount = int(reps.size());
  EdgeUse(m.tris, m.merged, m.openTopo, m.nonManifoldTopo, &m.degenerateTopo);
  // exact-coordinate merge, for comparison
  std::map<std::array<double, 3>, int> byCoord;
  std::vector<int> ex(n);
  for (int i = 0; i < n; ++i) ex[i] = byCoord.emplace(m.nodes[i], i).first->second;
  m.exactCount = int(byCoord.size());
  EdgeUse(m.tris, ex, m.openExact, m.nonManifoldExact, nullptr);
  return m;
}

// ------------------------------------------------------------------------------------------------
// STEP with names and colours (XCAF)
// ------------------------------------------------------------------------------------------------

struct Part
{
  std::string name;
  TopoDS_Shape shape;
  double r, g, b;
};

static occ::handle<TDocStd_Document> NewDoc(double unitMetres)
{
  occ::handle<TDocStd_Document> doc;
  XCAFApp_Application::GetApplication()->NewDocument("MDTV-XCAF", doc);
  XCAFDoc_DocumentTool::SetLengthUnit(doc, unitMetres);
  return doc;
}

static occ::handle<TDocStd_Document> DocOf(const std::vector<Part>& parts)
{
  occ::handle<TDocStd_Document> doc = NewDoc(1e-6);
  occ::handle<XCAFDoc_ShapeTool> st = XCAFDoc_DocumentTool::ShapeTool(doc->Main());
  occ::handle<XCAFDoc_ColorTool> ct = XCAFDoc_DocumentTool::ColorTool(doc->Main());
  for (const Part& p : parts)
  {
    TDF_Label l = st->AddShape(p.shape, false);
    TDataStd_Name::Set(l, TCollection_ExtendedString(p.name.c_str()));
    ct->SetColor(l, Quantity_Color(p.r, p.g, p.b, Quantity_TOC_RGB), XCAFDoc_ColorSurf);
  }
  return doc;
}

struct ReadPart
{
  std::string name;
  bool hasColor = false;
  double r = 0, g = 0, b = 0;
  TopoDS_Shape shape;
};

static void Collect(const occ::handle<XCAFDoc_ShapeTool>& st, const occ::handle<XCAFDoc_ColorTool>& ct, const TDF_Label& l,
                    std::vector<ReadPart>& out)
{
  TDF_Label ref = l;
  if (st->IsReference(l)) st->GetReferredShape(l, ref);
  if (st->IsAssembly(ref))
  {
    NCollection_Sequence<TDF_Label> comps;
    st->GetComponents(ref, comps);
    for (int i = 1; i <= comps.Length(); ++i) Collect(st, ct, comps(i), out);
    return;
  }
  ReadPart p;
  occ::handle<TDataStd_Name> nm;
  if (l.FindAttribute(TDataStd_Name::GetID(), nm) || ref.FindAttribute(TDataStd_Name::GetID(), nm))
    p.name = TCollection_AsciiString(nm->Get()).ToCString();
  Quantity_Color c;
  for (TDF_Label q : {l, ref})
    for (XCAFDoc_ColorType ty : {XCAFDoc_ColorSurf, XCAFDoc_ColorGen, XCAFDoc_ColorCurv})
      if (!p.hasColor && ct->GetColor(q, ty, c)) { p.hasColor = true; c.Values(p.r, p.g, p.b, Quantity_TOC_RGB); }
  p.shape = st->GetShape(l);
  if (!p.hasColor && ct->GetColor(p.shape, XCAFDoc_ColorSurf, c)) { p.hasColor = true; c.Values(p.r, p.g, p.b, Quantity_TOC_RGB); }
  out.push_back(p);
}

static std::vector<ReadPart> ReadStep(STEPCAFControl_Reader& rd, double docUnitMetres, std::string* units)
{
  rd.SetNameMode(true);
  rd.SetColorMode(true);
  occ::handle<TDocStd_Document> doc = NewDoc(docUnitMetres);
  std::vector<ReadPart> out;
  if (units)
  {
    NCollection_Sequence<TCollection_AsciiString> len, ang, sol;
    rd.ChangeReader().FileUnits(len, ang, sol);
    *units = "";
    for (int i = 1; i <= len.Length(); ++i) *units += (i > 1 ? "," : "") + std::string(len(i).ToCString());
  }
  if (!rd.Transfer(doc)) return out;
  occ::handle<XCAFDoc_ShapeTool> st = XCAFDoc_DocumentTool::ShapeTool(doc->Main());
  occ::handle<XCAFDoc_ColorTool> ct = XCAFDoc_DocumentTool::ColorTool(doc->Main());
  NCollection_Sequence<TDF_Label> free;
  st->GetFreeShapes(free);
  for (int i = 1; i <= free.Length(); ++i) Collect(st, ct, free(i), out);
  return out;
}

// ------------------------------------------------------------------------------------------------
// noop -- process start and library load only (Q12)
// ------------------------------------------------------------------------------------------------

static int CmdNoop()
{
  // Every toolkit the executable links is loaded before main (q12/process-start.txt lists them).
  std::printf("noop %s\n", OCC_VERSION_COMPLETE);
  return 0;
}

// ------------------------------------------------------------------------------------------------
// selftest -- the fast subset brief 62's smoke test makes (box - cylinder, a fillet, STEP in memory)
// ------------------------------------------------------------------------------------------------

static int CmdSelftest()
{
  auto t0 = Clock::now();
  bool ok = true;
  TopoDS_Shape cut = BoredLid(0);
  Counts cc = Count(cut);
  double vCut = Volume(cut), vCutRef = 1000.0 * 800 * 500 - M_PI * 150 * 150 * 500;
  bool cutOk = Valid(cut) && cc.solids == 1 && cc.faces == 7 && std::abs(vCut / vCutRef - 1) < 1e-9;
  ok &= cutOk;

  TopoDS_Shape fil = BoredLid(50);
  Counts fc = Count(fil);
  // Pappus: the removed spandrel (area r^2(1 - pi/4)) swept at radius R + r(10 - 3 pi)/(3(4 - pi)).
  double r = 50, R = 150, xbar = r * (10 - 3 * M_PI) / (3 * (4 - M_PI));
  double vFilRef = vCutRef - r * r * (1 - M_PI / 4) * 2 * M_PI * (R + xbar);
  double vFil = Volume(fil);
  bool filOk = Valid(fil) && fc.solids == 1 && fc.faces == 8 && std::abs(vFil / vFilRef - 1) < 1e-6;
  ok &= filOk;

  std::string brepBefore = BrepText(fil);
  // STEP, written and read back in memory, with a name and a colour.
  occ::handle<TDocStd_Document> doc = DocOf({{"lid", fil, 0.83, 0.69, 0.22}});
  STEPCAFControl_Writer wr;
  wr.SetNameMode(true);
  wr.SetColorMode(true);
  DESTEP_Parameters prm;
  prm.InitFromStatic();
  prm.WriteUnit = UnitsMethods_LengthUnit_Micron;
  std::ostringstream os;
  bool wrote = wr.Transfer(doc, prm) && wr.WriteStream(os) == IFSelect_RetDone;
  std::istringstream is(os.str());
  STEPCAFControl_Reader rd;
  bool readOk = rd.ReadStream("selftest.step", is) == IFSelect_RetDone;
  std::vector<ReadPart> parts = readOk ? ReadStep(rd, 1e-6, nullptr) : std::vector<ReadPart>{};
  bool stepOk = wrote && readOk && parts.size() == 1 && parts[0].name == "lid" && parts[0].hasColor
             && std::abs(Volume(parts[0].shape) / vFil - 1) < 1e-9;
  ok &= stepOk;

  std::string brep = brepBefore, brepAfter = BrepText(fil);
  std::printf("selftest %s  OCCT %s  %s\n", ok ? "PASS" : "FAIL", OCC_VERSION_COMPLETE, Rid());
  std::printf("  box-cylinder   %s  valid=%d solids/shells/faces/edges/vertices=%s volume=%s (ref %s)\n", cutOk ? "ok" : "FAIL",
              Valid(cut), cc.Str().c_str(), F(vCut, 12).c_str(), F(vCutRef, 12).c_str());
  std::printf("  fillet r=50um  %s  valid=%d counts=%s volume=%s (Pappus %s)\n", filOk ? "ok" : "FAIL", Valid(fil), fc.Str().c_str(),
              F(vFil, 12).c_str(), F(vFilRef, 12).c_str());
  std::printf("  STEP in memory %s  bytes=%zu parts=%zu name=%s colour=%d\n", stepOk ? "ok" : "FAIL", os.str().size(), parts.size(),
              parts.empty() ? "-" : parts[0].name.c_str(), parts.empty() ? 0 : parts[0].hasColor);
  std::printf("  brep v3 sha256 %s (%zu bytes)  -- compare across RIDs (Q7)\n", Sha256(brep).c_str(), brep.size());
  std::printf("  STEP export left the shape's B-rep %s (%zu bytes after)\n", brepAfter == brep ? "unchanged" : "CHANGED", brepAfter.size());
  std::printf("  elapsed %s ms\n", F(Ms(t0), 4).c_str());
  return ok ? 0 : 1;
}

// ------------------------------------------------------------------------------------------------
// Q4 -- Gmsh reads our shape (D12)
// ------------------------------------------------------------------------------------------------

static std::string BoxQuery(const Box6& b)
{
  return "Surface In BoundingBox{" + F(b.x0, 12) + " - e, " + F(b.y0, 12) + " - e, " + F(b.z0, 12) + " - e, " + F(b.x1, 12)
       + " + e, " + F(b.y1, 12) + " + e, " + F(b.z1, 12) + " + e}";
}

static std::string Q4Geo(const std::string& importLine, const std::string& unitLine, const Box6& fillet, const Box6& top)
{
  std::ostringstream g;
  g << "// brief-em3d-61 Q4 -- spike material. Import an OCCT-written part, fragment it with an air box,\n"
       "// recover two named faces by a tight bounding-box query, mesh second order. Units: micrometres.\n"
       "SetFactory(\"OpenCASCADE\");\n"
       "Geometry.OCCBooleanPreserveNumbering = 1;\n"
       "Geometry.OCCBoundsUseStl = 1;\n"
    << unitLine
    << "e = 1e-3;\n"
    << importLine
    << "bb[] = BoundingBox Volume{part[0]};\n"
       "Printf(\"Q4 part box %g %g %g .. %g %g %g\", bb[0], bb[1], bb[2], bb[3], bb[4], bb[5]);\n"
       "bx = newv; Box(bx) = {-500, -500, -500, 2000, 1800, 1500};\n"
       "frag[] = BooleanFragments{ Volume{part[]}; Delete; }{ Volume{bx}; Delete; };\n"
       "allV[] = Volume{:};\n"
       "allS[] = Surface{:};\n"
       "fil[] = " << BoxQuery(fillet) << ";\n"
       "top[] = " << BoxQuery(top) << ";\n"
       "Printf(\"Q4 volumes %g surfaces %g fillet-hits %g top-hits %g\", #allV[], #allS[], #fil[], #top[]);\n"
       "Mesh.MeshSizeMax = 150;\n"
       "Mesh.MeshSizeFromCurvature = 12;\n"
       "Mesh.ElementOrder = 2;\n"
       "Mesh.HighOrderOptimize = 2;\n";
  return g.str();
}

static int CmdQ4()
{
  std::string d = Dir("q4");
  TopoDS_Shape part = BoredLid(50);
  // the two faces the .geo must recover: the fillet (torus) and the lid's top plane
  Box6 fillet{}, top{};
  std::ostringstream faces;
  for (TopExp_Explorer x(part, TopAbs_FACE); x.More(); x.Next())
  {
    const TopoDS_Face& f = TopoDS::Face(x.Current());
    Box6 b = Tight(f);
    std::string k = SurfKind(f);
    if (k == "torus") fillet = b;
    if (k == "plane" && std::abs(b.z0 - 500) < 1e-9 && std::abs(b.z1 - 500) < 1e-9) top = b;
    faces << "  " << k << " [" << F(b.x0, 12) << "," << F(b.y0, 12) << "," << F(b.z0, 12) << " .. " << F(b.x1, 12) << ","
          << F(b.y1, 12) << "," << F(b.z1, 12) << "]\n";
  }
  std::ostringstream info;
  info << "part: 1000 x 800 x 500 um lid, r = 150 um bore, bore rim filleted at 50 um. Volume " << F(Volume(part), 12)
       << ", valid " << Valid(part) << ", counts " << Count(part).Str() << "\nfaces (tight boxes, um):\n" << faces.str();
  for (int v = 1; v <= 3; ++v)
  {
    std::string name = "part-v" + std::to_string(v) + ".brep";
    std::string text = BrepText(part, TopTools_FormatVersion(v));
    WriteFile(d + name, text);
    info << name << ": " << text.size() << " bytes, sha256 " << Sha256(text) << ", header: " << text.substr(text.find_first_not_of('\n'), text.find('\n', text.find_first_not_of('\n')) - text.find_first_not_of('\n')) << "\n";
    WriteFile(d + "brep-v" + std::to_string(v) + ".geo",
              Q4Geo("part[] = ShapeFromFile(\"" + name + "\");\n", "", fillet, top));
  }
  // STEP, written in micrometres (AP214)
  occ::handle<TDocStd_Document> doc = DocOf({{"lid", part, 0.83, 0.69, 0.22}});
  STEPCAFControl_Writer wr;
  wr.SetNameMode(true);
  wr.SetColorMode(true);
  DESTEP_Parameters prm;
  prm.InitFromStatic();
  prm.WriteUnit = UnitsMethods_LengthUnit_Micron;
  bool ok = wr.Perform(doc, (d + "part-um.step").c_str(), prm);
  info << "part-um.step written " << ok << "\n";
  WriteFile(d + "step-um-target-um.geo",
            Q4Geo("part[] = ShapeFromFile(\"part-um.step\");\n", "Geometry.OCCTargetUnit = \"UM\";\n", fillet, top));
  WriteFile(d + "step-um-default-unit.geo", Q4Geo("part[] = ShapeFromFile(\"part-um.step\");\n", "", fillet, top));
  WriteFile(d + "part.txt", info.str());
  std::printf("q4 wrote %s (3 brep versions, 1 step, 5 .geo); run q4/run.sh for the Gmsh half\n", d.c_str());
  return 0;
}

// ------------------------------------------------------------------------------------------------
// Q5 -- coincident faces in integer DBU
// ------------------------------------------------------------------------------------------------

// 2D convex clip (Sutherland-Hodgman) for the rotated case's analytic intersection.
using P2 = std::array<double, 2>;
static std::vector<P2> Clip(const std::vector<P2>& poly, const std::vector<P2>& clip)
{
  std::vector<P2> out = poly;
  for (size_t i = 0; i < clip.size(); ++i)
  {
    P2 a = clip[i], b = clip[(i + 1) % clip.size()];
    auto side = [&](const P2& p) { return (b[0] - a[0]) * (p[1] - a[1]) - (b[1] - a[1]) * (p[0] - a[0]); };
    std::vector<P2> in = out;
    out.clear();
    for (size_t j = 0; j < in.size(); ++j)
    {
      P2 p = in[j], q = in[(j + 1) % in.size()];
      double sp = side(p), sq = side(q);
      if (sp >= 0) out.push_back(p);
      if ((sp >= 0) != (sq >= 0))
      {
        double t = sp / (sp - sq);
        out.push_back({p[0] + t * (q[0] - p[0]), p[1] + t * (q[1] - p[1])});
      }
    }
  }
  return out;
}

static double PolyArea(const std::vector<P2>& p)
{
  double a = 0;
  for (size_t i = 0; i < p.size(); ++i) a += p[i][0] * p[(i + 1) % p.size()][1] - p[(i + 1) % p.size()][0] * p[i][1];
  return std::abs(a) / 2;
}

struct Q5Case
{
  std::string name, note;
  TopoDS_Shape a, b;
  double va, vb, vab;
};

static TopoDS_Shape Rot30X(const TopoDS_Shape& s)
{
  gp_Trsf t;
  t.SetRotation(gp_Ax1(gp_Pnt(0, 0, 0), gp_Dir(1, 0, 0)), 30 * M_PI / 180);
  return BRepBuilderAPI_Transform(s, t, true).Shape();
}

static int CmdQ5()
{
  std::string d = Dir("q5");
  std::vector<Q5Case> cases;
  double V1 = 1e9;
  cases.push_back({"shared-face-int", "A [0,1000]^3; B [1000,2000]x[0,1000]x[0,1000] -- the whole face shared, integer um",
                   MakeBox(0, 0, 0, 1000, 1000, 1000), MakeBox(1000, 0, 0, 2000, 1000, 1000), V1, V1, 0});
  cases.push_back({"partial-face-int", "A [0,1000]^3; B [1000,2000]x[250,750]x[0,500] -- part of A's xmax shared",
                   MakeBox(0, 0, 0, 1000, 1000, 1000), MakeBox(1000, 250, 0, 2000, 750, 500), V1, 1000.0 * 500 * 500, 0});
  double x = 1234567 / 1000.0; // DBU 1234567 at DbuPerMicron 1000: 1234.567 um, not a binary fraction
  cases.push_back({"shared-face-dbu", "shared plane at 1234567 DBU / 1000 = 1234.567 um, the same double on both sides",
                   MakeBox(0, 0, 0, x, 1000, 1000), MakeBox(x, 0, 0, x + 1000, 1000, 1000), x * 1e6, 1e9, 0});
  double x2 = 1000 + 234567 / 1000.0; // the same plane reached by another arithmetic path
  cases.push_back({"shared-face-dbu-path", std::string("B's min reached as 1000 + 234567/1000 = ") + F(x2, 17) + " vs " + F(x, 17)
                                               + (x2 == x ? " (equal)" : " (differ)"),
                   MakeBox(0, 0, 0, x, 1000, 1000), MakeBox(x2, 0, 0, x2 + 1000, 1000, 1000), x * 1e6, 1e9, 0});
  double xu = std::nextafter(x, 1e9), xd = std::nextafter(x, 0.0);
  cases.push_back({"gap-1ulp", "B starts one ULP above A's max (" + F(xu - x, 3) + " um gap)", MakeBox(0, 0, 0, x, 1000, 1000),
                   MakeBox(xu, 0, 0, xu + 1000, 1000, 1000), x * 1e6, 1e9, 0});
  cases.push_back({"overlap-1ulp", "B starts one ULP below A's max (" + F(x - xd, 3) + " um overlap)", MakeBox(0, 0, 0, x, 1000, 1000),
                   MakeBox(xd, 0, 0, xd + 1000, 1000, 1000), x * 1e6, 1e9, 0}); // 2.3e-7 um^3 of overlap: expected to read as touching
  double r = 333, rv = 2000.0 / 3; // DbuPerMicron 3: DBU 1000 -> 333.333.. um and DBU 2000 -> 666.666.. um
  (void)r;
  double a3 = 1000 / 3.0;
  cases.push_back({"shared-face-dbu3", "DbuPerMicron = 3: shared plane at DBU 1000 / 3 = " + F(a3, 17) + " um",
                   MakeBox(0, 0, 0, a3, 1000, 1000), MakeBox(a3, 0, 0, rv, 1000, 1000), a3 * 1e6, (rv - a3) * 1e6, 0});
  // rotated: A and B share their y = 1000 face, both rotated 30 deg about X
  cases.push_back({"rot30-shared-face", "A [0,1000]^3 and B [0,1000]x[1000,2000]x[0,1000], both rotated 30 deg about X",
                   Rot30X(MakeBox(0, 0, 0, 1000, 1000, 1000)), Rot30X(MakeBox(0, 1000, 0, 1000, 2000, 1000)), V1, V1, 0});
  {
    // rotated A against an axis-aligned slab; analytic overlap = x-overlap * clipped y-z area
    double c = std::cos(M_PI / 6), s = std::sin(M_PI / 6);
    std::vector<P2> sq;
    for (P2 p : std::vector<P2>{{0, 0}, {1000, 0}, {1000, 1000}, {0, 1000}}) sq.push_back({c * p[0] - s * p[1], s * p[0] + c * p[1]});
    std::vector<P2> slab{{-200, 300}, {1200, 300}, {1200, 700}, {-200, 700}};
    double area = PolyArea(Clip(sq, slab));
    cases.push_back({"rot30-vs-axis", "A [0,1000]^3 rotated 30 deg about X; B [200,800]x[-200,1200]x[300,700], axis-aligned",
                     Rot30X(MakeBox(0, 0, 0, 1000, 1000, 1000)), MakeBox(200, -200, 300, 800, 1200, 700), V1, 600.0 * 1400 * 400,
                     600.0 * area});
  }

  struct Mode { std::string name; double fuzzy; bool glue; };
  std::vector<Mode> modes{{"fuzzy 0", 0, false}, {"fuzzy 0 + GlueShift", 0, true}, {"fuzzy 1e-4 um", 1e-4, false}};
  std::ostringstream t;
  t << "# Q5 -- coincident faces (generated by `occt_probe q5`)\n\nOCCT " << OCC_VERSION_COMPLETE << ", " << Rid()
    << ". Lengths in micrometres. Counts are solids/shells/faces/edges/vertices. `rel err` is the result volume against the "
       "analytic value. A **sliver** is a face below 1e-6 of the smallest input face's area.\n\n";
  for (auto& c : cases)
  {
    double minFace = std::min(MinFaceArea(c.a), MinFaceArea(c.b));
    t << "## " << c.name << "\n\n" << c.note << "\n\n| op | mode | done | warn | valid | counts | volume | rel err | slivers |\n|---|---|---|---|---|---|---|---|---|\n";
    for (int op = 0; op < 3; ++op)
      for (auto& m : modes)
      {
        std::unique_ptr<BRepAlgoAPI_BooleanOperation> b;
        if (op == 0) b = std::make_unique<BRepAlgoAPI_Fuse>();
        if (op == 1) b = std::make_unique<BRepAlgoAPI_Cut>();
        if (op == 2) b = std::make_unique<BRepAlgoAPI_Common>();
        NCollection_List<TopoDS_Shape> args, tools;
        args.Append(c.a);
        tools.Append(c.b);
        b->SetArguments(args);
        b->SetTools(tools);
        b->SetFuzzyValue(m.fuzzy);
        if (m.glue) b->SetGlue(BOPAlgo_GlueShift);
        b->Build();
        TopoDS_Shape res = b->IsDone() ? b->Shape() : TopoDS_Shape();
        double ref = op == 0 ? c.va + c.vb - c.vab : op == 1 ? c.va - c.vab : c.vab;
        double v   = Volume(res);
        double err = ref == 0 ? v : v / ref - 1;
        const char* opn = op == 0 ? "Unite" : op == 1 ? "Subtract" : "Intersect";
        t << "| " << opn << " | " << m.name << " | " << b->IsDone() << " | " << b->HasWarnings() << " | " << Valid(res) << " | "
          << Count(res).Str() << " | " << F(v, 13) << " | " << (ref == 0 ? "abs " + F(v, 3) : F(err, 3)) << " | "
          << (res.IsNull() ? 0 : Slivers(res, 1e-6 * minFace)) << " |\n";
      }
    t << "\n";
  }
  WriteFile(d + "RESULTS.md", t.str());
  std::printf("q5 wrote %sRESULTS.md (%zu cases x 3 ops x 3 modes)\n", d.c_str(), cases.size());
  return 0;
}

// ------------------------------------------------------------------------------------------------
// Q6 -- fillets and chamfers on curved edges, and across a tangent chain
// ------------------------------------------------------------------------------------------------

static std::vector<TopoDS_Edge> CircleEdgesAtZ(const TopoDS_Shape& s, double z)
{
  std::vector<TopoDS_Edge> out;
  Shapes all;
  TopExp::MapShapes(s, TopAbs_EDGE, all); // unique edges: an explorer visits a shared edge once per face
  for (int i = 1; i <= all.Extent(); ++i)
  {
    TopoDS_Shape cur = all(i);
    Box6 b = Tight(cur);
    if (std::abs(b.z0 - z) < 1e-6 && std::abs(b.z1 - z) < 1e-6)
    {
      double t0, t1;
      occ::handle<Geom_Curve> c = BRep_Tool::Curve(TopoDS::Edge(cur), t0, t1);
      if (!c.IsNull() && c->DynamicType()->Name() == std::string("Geom_Circle")) out.push_back(TopoDS::Edge(cur));
    }
  }
  return out;
}

static int CmdQ6()
{
  std::string d = Dir("q6");
  std::ostringstream t;
  t << "# Q6 -- fillets and chamfers on curved edges (generated by `occt_probe q6`)\n\nOCCT " << OCC_VERSION_COMPLETE << ", " << Rid()
    << ". Lengths in micrometres. Analytic volumes by Pappus: a fillet of radius r removes or adds the spandrel r^2(1 - pi/4), "
       "whose centroid sits r(10 - 3 pi)/(3(4 - pi)) = 0.2234 r from the corner; a symmetric chamfer d the triangle d^2/2, "
       "centroid d/3 from the corner.\n\n| case | op | done | valid | counts | faces by kind | volume | analytic | rel err | ms |\n"
       "|---|---|---|---|---|---|---|---|---|---|\n";
  const double xb = (10 - 3 * M_PI) / (3 * (4 - M_PI));
  auto kinds = [](const TopoDS_Shape& s) {
    std::map<std::string, int> m;
    for (TopExp_Explorer x(s, TopAbs_FACE); x.More(); x.Next()) m[SurfKind(TopoDS::Face(x.Current()))]++;
    std::string o;
    for (auto& [k, n] : m) o += (o.empty() ? "" : " ") + k + ":" + std::to_string(n);
    return o;
  };
  auto row = [&](const std::string& cs, const std::string& op, bool done, const TopoDS_Shape& s, double ref, double ms, const std::string& file) {
    double v = s.IsNull() ? 0 : Volume(s);
    t << "| " << cs << " | " << op << " | " << done << " | " << Valid(s) << " | " << Count(s).Str() << " | " << (s.IsNull() ? "" : kinds(s))
      << " | " << F(v, 13) << " | " << F(ref, 13) << " | " << F(v / ref - 1, 3) << " | " << F(ms, 4) << " |\n";
    if (!s.IsNull() && !file.empty()) WriteFile(d + file, BrepText(s));
  };

  // 1. a pin united with a block; the circular intersection edge filleted at 50 um, then chamfered at 50 um
  {
    double R = 100, H = 300, r = 50;
    TopoDS_Shape block = MakeBox(0, 0, 0, 1000, 1000, H), pin = MakeCyl(500, 500, 0, R, 800);
    double vUnion = 1e6 * H + M_PI * R * R * (800 - H);
    auto t0 = Clock::now();
    TopoDS_Shape u = BRepAlgoAPI_Fuse(block, pin).Shape();
    row("pin-in-block", "Unite", true, u, vUnion, Ms(t0), "");
    auto edges = CircleEdgesAtZ(u, H);
    t0 = Clock::now();
    BRepFilletAPI_MakeFillet mf(u);
    for (auto& e : edges) mf.Add(r, e);
    mf.Build();
    double add = r * r * (1 - M_PI / 4) * 2 * M_PI * (R + xb * r);
    row("pin-in-block", "Fillet r=50 on the circle (" + std::to_string(edges.size()) + " edge)", mf.IsDone(), mf.IsDone() ? mf.Shape() : TopoDS_Shape(),
        vUnion + add, Ms(t0), "pin-fillet.brep");
    t0 = Clock::now();
    BRepFilletAPI_MakeChamfer mc(u);
    for (auto& e : edges) mc.Add(r, e);
    mc.Build();
    row("pin-in-block", "Chamfer d=50 on the circle", mc.IsDone(), mc.IsDone() ? mc.Shape() : TopoDS_Shape(),
        vUnion + r * r / 2 * 2 * M_PI * (R + r / 3), Ms(t0), "pin-chamfer.brep");
  }
  // 2. a lid with a bore subtracted; the bore's top rim filleted at 25 um, then chamfered
  {
    double R = 150, H = 500, r = 25;
    TopoDS_Shape lid = MakeBox(0, 0, 0, 1000, 800, H), bore = MakeCyl(500, 400, -100, R, 700);
    double vCut = 1000.0 * 800 * H - M_PI * R * R * H;
    auto t0 = Clock::now();
    TopoDS_Shape c = BRepAlgoAPI_Cut(lid, bore).Shape();
    row("bore-in-lid", "Subtract", true, c, vCut, Ms(t0), "");
    auto edges = CircleEdgesAtZ(c, H);
    t0 = Clock::now();
    BRepFilletAPI_MakeFillet mf(c);
    for (auto& e : edges) mf.Add(r, e);
    mf.Build();
    row("bore-in-lid", "Fillet r=25 on the rim", mf.IsDone(), mf.IsDone() ? mf.Shape() : TopoDS_Shape(),
        vCut - r * r * (1 - M_PI / 4) * 2 * M_PI * (R + xb * r), Ms(t0), "lid-fillet.brep");
    t0 = Clock::now();
    BRepFilletAPI_MakeChamfer mc(c);
    for (auto& e : edges) mc.Add(r, e);
    mc.Build();
    row("bore-in-lid", "Chamfer d=25 on the rim", mc.IsDone(), mc.IsDone() ? mc.Shape() : TopoDS_Shape(),
        vCut - r * r / 2 * 2 * M_PI * (R + r / 3), Ms(t0), "lid-chamfer.brep");
  }
  // 3. a tangent chain: a rounded rectangle (1000 x 600, corner radius 100) extruded 200; all top edges
  {
    double W = 1000, D = 600, Rc = 100, H = 200, r = 30;
    // outline from 4 lines and 4 arcs
    TopoDS_Shape box = MakeBox(0, 0, 0, W, D, H);
    BRepFilletAPI_MakeFillet corners(box);
    for (TopExp_Explorer x(box, TopAbs_EDGE); x.More(); x.Next())
    {
      Box6 b = Tight(x.Current());
      if (std::abs(b.z1 - b.z0 - H) < 1e-9) corners.Add(Rc, TopoDS::Edge(x.Current()));
    }
    corners.Build();
    TopoDS_Shape rr = corners.Shape();
    double vPrism = (W * D - (4 - M_PI) * Rc * Rc) * H;
    row("rounded-rect", "Prism (vertical corners rounded r=100)", corners.IsDone(), rr, vPrism, 0, "");
    std::vector<TopoDS_Edge> top;
    Shapes rrEdges;
    TopExp::MapShapes(rr, TopAbs_EDGE, rrEdges);
    for (int i = 1; i <= rrEdges.Extent(); ++i)
    {
      Box6 b = Tight(rrEdges(i));
      if (std::abs(b.z0 - H) < 1e-6 && std::abs(b.z1 - H) < 1e-6) top.push_back(TopoDS::Edge(rrEdges(i)));
    }
    auto t0 = Clock::now();
    BRepFilletAPI_MakeFillet mf(rr);
    mf.Add(r, top[0]); // one edge: the tangent chain propagates to all eight
    mf.Build();
    double path    = 2 * (W - 2 * Rc) + 2 * (D - 2 * Rc) + 2 * M_PI * (Rc - xb * r);
    double removed = r * r * (1 - M_PI / 4) * path;
    row("rounded-rect", "Fillet r=30 on ONE top edge (" + std::to_string(top.size()) + " top edges; NbContours " +
                            std::to_string(mf.NbContours()) + ", edges in contour 1: " + std::to_string(mf.NbEdges(1)) + ")",
        mf.IsDone(), mf.IsDone() ? mf.Shape() : TopoDS_Shape(), vPrism - removed, Ms(t0), "chain-fillet.brep");
    t0 = Clock::now();
    BRepFilletAPI_MakeChamfer mc(rr);
    mc.Add(r, top[0]);
    mc.Build();
    double pathC = 2 * (W - 2 * Rc) + 2 * (D - 2 * Rc) + 2 * M_PI * (Rc - r / 3);
    row("rounded-rect", "Chamfer d=30 on ONE top edge", mc.IsDone(), mc.IsDone() ? mc.Shape() : TopoDS_Shape(), vPrism - r * r / 2 * pathC,
        Ms(t0), "chain-chamfer.brep");
  }
  WriteFile(d + "RESULTS.md", t.str());
  std::printf("q6 wrote %sRESULTS.md\n", d.c_str());
  return 0;
}

// ------------------------------------------------------------------------------------------------
// Q7 -- names survive (overview section 1g), and determinism
// ------------------------------------------------------------------------------------------------

struct Q7Out
{
  std::string name, table, brep;
};

static std::vector<Q7Out> Q7Run(bool parallel)
{
  std::vector<Q7Out> out;
  auto boolean = [&](const std::string& label, BRepAlgoAPI_BooleanOperation& op, const NamedResult& blank,
                     const std::vector<std::pair<std::string, const NamedResult*>>& tools) {
    NCollection_List<TopoDS_Shape> a, t;
    a.Append(blank.shape);
    for (auto& [n, r] : tools) t.Append(r->shape);
    op.SetArguments(a);
    op.SetTools(t);
    op.SetRunParallel(parallel);
    op.Build();
    std::vector<std::pair<std::string, const NamedResult*>> operands{{"", &blank}};
    for (auto& x : tools) operands.push_back(x);
    NamedResult r = NameThrough(op, op.Shape(), operands);
    EdgeNaming en = NameEdges(r);
    std::ostringstream s;
    s << "# " << label << "\n# valid " << Valid(r.shape) << ", counts " << Count(r.shape).Str() << ", faces unnamed " << r.unnamed
      << ", faces with >1 name " << r.multiNamed << "\n# edges " << en.edges.size() << ", seam edges " << en.seams << ", degenerate "
      << en.degenerate << ", face pairs shared by >1 edge: " << en.samePairGroups << " (" << en.samePairEdges << " edges)\n\n"
      << "## history (operand face -> result faces)\n" << r.table << "\n## result faces\n" << DescribeFaces(r) << "\n## edges\n" << en.table;
    out.push_back({label, s.str(), BrepText(r.shape)});
    return std::make_pair(r, en);
  };

  NamedResult lid    = FromNamed(NameBox(MakeBox(0, 0, 0, 1000, 800, 500)));
  NamedResult cavity = FromNamed(NameCyl(MakeCyl(500, 400, -100, 150, 700)));
  NamedResult slot   = FromNamed(NameBox(MakeBox(400, -100, 300, 600, 900, 600)));
  NamedResult pin    = FromNamed(NameCyl(MakeCyl(500, 400, -100, 100, 800)));

  BRepAlgoAPI_Cut c1;
  auto [bored, boredEdges] = boolean("subtract-cavity (lid - cavity: a bore)", c1, lid, {{"cavity", &cavity}});
  BRepAlgoAPI_Cut c2;
  boolean("subtract-slot (lid - slot: zmax split in two)", c2, lid, {{"slot", &slot}});
  BRepAlgoAPI_Fuse f1;
  boolean("unite-pin (lid + pin)", f1, lid, {{"pin", &pin}});
  BRepAlgoAPI_Cut c3;
  boolean("subtract-two (lid - cavity - slot)", c3, lid, {{"cavity", &cavity}, {"slot", &slot}});
  // a half-sunk horizontal cylinder that stops short of both y faces: its side meets zmax along two lines
  NamedResult trench = FromNamed(NameCyl(BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(500, 200, 500), gp_Dir(0, 1, 0)), 100, 400).Shape()));
  BRepAlgoAPI_Cut c4;
  boolean("subtract-trench (lid - a half-sunk horizontal cylinder: one face pair, two edges)", c4, lid, {{"trench", &trench}});

  // fillet and chamfer on the bore's rim: the edge between cavity:side and zmax
  std::string rim = "cavity:side|zmax";
  {
    BRepFilletAPI_MakeFillet mf(bored.shape);
    mf.Add(25, EdgeNamed(boredEdges, rim));
    mf.Build();
    NamedResult r  = NameBlend(mf, mf, mf.Shape(), bored, boredEdges, {rim}, "fillet");
    EdgeNaming  en = NameEdges(r);
    std::ostringstream s;
    s << "# fillet-rim (fillet r=25 on " << rim << ")\n# valid " << Valid(r.shape) << ", counts " << Count(r.shape).Str()
      << ", faces unnamed " << r.unnamed << ", faces with >1 name " << r.multiNamed << "\n# edges " << en.edges.size() << ", seam edges "
      << en.seams << ", face pairs shared by >1 edge: " << en.samePairGroups << " (" << en.samePairEdges << " edges)\n\n## history\n"
      << r.table << "\n## result faces\n" << DescribeFaces(r) << "\n## edges\n" << en.table;
    out.push_back({"fillet-rim", s.str(), BrepText(r.shape)});
  }
  {
    BRepFilletAPI_MakeChamfer mc(bored.shape);
    mc.Add(25, EdgeNamed(boredEdges, rim));
    mc.Build();
    NamedResult r = NameBlend(mc, mc, mc.Shape(), bored, boredEdges, {rim}, "chamfer");
    std::ostringstream s;
    s << "# chamfer-rim (chamfer d=25 on " << rim << ")\n# valid " << Valid(r.shape) << ", counts " << Count(r.shape).Str()
      << ", faces unnamed " << r.unnamed << ", faces with >1 name " << r.multiNamed << "\n\n## history\n" << r.table << "\n## result faces\n"
      << DescribeFaces(r);
    out.push_back({"chamfer-rim", s.str(), BrepText(r.shape)});
  }
  // a tangent chain: ONE edge listed, OCCT propagates to the rest -- do the propagated edges' faces get names?
  {
    TopoDS_Shape box = MakeBox(0, 0, 0, 1000, 600, 200);
    BRepFilletAPI_MakeFillet corners(box);
    Shapes be;
    TopExp::MapShapes(box, TopAbs_EDGE, be);
    for (int i = 1; i <= be.Extent(); ++i)
      if (std::abs(Tight(be(i)).z1 - Tight(be(i)).z0 - 200) < 1e-9) corners.Add(100, TopoDS::Edge(be(i)));
    corners.Build();
    NamedResult rr = NameBlend(corners, corners, corners.Shape(), FromNamed(NameBox(box)), NameEdges(FromNamed(NameBox(box))),
                               {"xmax|ymax", "xmax|ymin", "xmin|ymax", "xmin|ymin"}, "fillet");
    EdgeNaming ren = NameEdges(rr);
    std::string listed = "ymin|zmax";
    BRepFilletAPI_MakeFillet mf(rr.shape);
    mf.Add(30, EdgeNamed(ren, listed));
    mf.Build();
    std::vector<std::string> contour;
    for (int ie = 1; ie <= mf.NbEdges(1); ++ie)
      for (auto& [e, n] : ren.edges)
        if (e.IsSame(mf.Edge(1, ie))) contour.push_back(n);
    NamedResult onlyListed = NameBlend(mf, mf, mf.Shape(), rr, ren, {listed}, "fillet");
    NamedResult all        = NameBlend(mf, mf, mf.Shape(), rr, ren, contour, "fillet");
    std::ostringstream s;
    s << "# fillet-chain (a rounded rectangle; fillet r=30 given ONE edge, " << listed << ")\n# valid " << Valid(all.shape) << ", counts "
      << Count(all.shape).Str() << ", contour edges " << mf.NbEdges(1) << "\n# naming from the LISTED edge only: faces unnamed "
      << onlyListed.unnamed << "\n# naming from every edge in the contour (mf.Edge(1, i)): faces unnamed " << all.unnamed
      << ", faces with >1 name " << all.multiNamed << "\n\n## history (every contour edge)\n" << all.table << "\n## result faces\n" << DescribeFaces(all);
    out.push_back({"fillet-chain", s.str(), BrepText(all.shape)});
  }
  // a box's four top edges: corners where two fillets meet a sharp vertical edge
  {
    NamedResult box = FromNamed(NameBox(MakeBox(0, 0, 0, 1000, 800, 500)));
    EdgeNaming  be  = NameEdges(box);
    std::vector<std::string> top{"xmax|zmax", "xmin|zmax", "ymax|zmax", "ymin|zmax"};
    BRepFilletAPI_MakeFillet mf(box.shape);
    for (auto& e : top) mf.Add(50, EdgeNamed(be, e));
    mf.Build();
    NamedResult r = NameBlend(mf, mf, mf.Shape(), box, be, top, "fillet");
    std::ostringstream s;
    s << "# fillet-box-top (fillet r=50 on the four top edges of a box)\n# valid " << Valid(r.shape) << ", counts " << Count(r.shape).Str()
      << ", faces unnamed " << r.unnamed << ", faces with >1 name " << r.multiNamed << "\n\n## history\n" << r.table << "\n## result faces\n"
      << DescribeFaces(r);
    out.push_back({"fillet-box-top", s.str(), BrepText(r.shape)});
  }
  return out;
}

// Edges whose two faces (by identity, not by name) are shared with another edge -- the case #n exists for.
static std::pair<int, int> SamePairEdges(const TopoDS_Shape& s)
{
  Shapes faces;
  TopExp::MapShapes(s, TopAbs_FACE, faces);
  Ancestors anc;
  TopExp::MapShapesAndAncestors(s, TopAbs_EDGE, TopAbs_FACE, anc);
  std::map<std::pair<int, int>, int> n;
  for (int i = 1; i <= anc.Extent(); ++i)
  {
    if (BRep_Tool::Degenerated(TopoDS::Edge(anc.FindKey(i)))) continue;
    std::set<int> f;
    for (const TopoDS_Shape& x : anc(i)) f.insert(faces.FindIndex(x));
    int a = *f.begin(), b = *f.rbegin();
    n[{a, b}]++;
  }
  int groups = 0, edges = 0;
  for (auto& [k, c] : n)
    if (c > 1) { ++groups; edges += c; }
  return {groups, edges};
}

static int CmdQ7()
{
  std::string d = Dir("q7");
  std::vector<Q7Out> a = Q7Run(false), b = Q7Run(false), p = Q7Run(true);
  std::ostringstream h;
  bool same = true, sameParallel = true;
  for (size_t i = 0; i < a.size(); ++i)
  {
    bool s1 = a[i].brep == b[i].brep && a[i].table == b[i].table;
    bool s2 = a[i].brep == p[i].brep && a[i].table == p[i].table;
    same &= s1;
    sameParallel &= s2;
    std::string file = "history-" + a[i].name.substr(0, a[i].name.find(' ')) + ".txt";
    WriteFile(d + file, a[i].table);
    h << Sha256(a[i].brep) << "  " << a[i].name.substr(0, a[i].name.find(' ')) << ".brep  (" << a[i].brep.size()
      << " bytes; second run in-process " << (s1 ? "identical" : "DIFFERENT") << "; parallel run " << (s2 ? "identical" : "DIFFERENT") << ")\n";
  }
  // Q5 / Q6 shapes: how often does one face pair bound more than one edge?
  {
    auto rotBox = Rot30X(MakeBox(0, 0, 0, 1000, 1000, 1000));
    auto slab   = MakeBox(200, -200, 300, 800, 1200, 700);
    std::vector<std::pair<std::string, TopoDS_Shape>> shapes{
      {"q4/q10 part (bored lid, filleted rim)", BoredLid(50)},
      {"q5 rot30-vs-axis Unite", BRepAlgoAPI_Fuse(rotBox, slab).Shape()},
      {"q5 rot30-vs-axis Subtract", BRepAlgoAPI_Cut(rotBox, slab).Shape()},
      {"q5 partial-face-int Unite", BRepAlgoAPI_Fuse(MakeBox(0, 0, 0, 1000, 1000, 1000), MakeBox(1000, 250, 0, 2000, 750, 500)).Shape()}};
    for (const char* f : {"pin-fillet", "pin-chamfer", "lid-fillet", "lid-chamfer", "chain-fillet", "chain-chamfer"})
    {
      TopoDS_Shape sh;
      BRep_Builder bb;
      if (BRepTools::Read(sh, (g_out + "/q6/" + f + ".brep").c_str(), bb)) shapes.push_back({std::string("q6 ") + f, sh});
    }
    std::ostringstream o;
    o << "# edges sharing one face pair (by face identity), in the Q5 and Q6 shapes\n";
    for (auto& [n, sh] : shapes)
    {
      auto [g, e] = SamePairEdges(sh);
      o << "  " << n << ": " << g << " face pair(s) bound more than one edge (" << e << " edges)\n";
    }
    WriteFile(d + "same-face-pair.txt", o.str());
  }
  std::string hashes = h.str();
  WriteFile(d + std::string("hashes-") + Rid() + ".txt", hashes);
  std::printf("q7 %s: in-process repeat %s, parallel %s; hashes in %shashes-%s.txt\n", same && sameParallel ? "DETERMINISTIC" : "NOT DETERMINISTIC",
              same ? "identical" : "DIFFERENT", sameParallel ? "identical" : "DIFFERENT", d.c_str(), Rid());
  return 0;
}

// ------------------------------------------------------------------------------------------------
// Q8 -- STEP round trip
// ------------------------------------------------------------------------------------------------

static int CmdQ8()
{
  std::string d = Dir("q8");
  std::vector<Part> parts{{"lid", BoredLid(50), 0.83, 0.69, 0.22},
                          {"pin", MakeCyl(500, 400, 500, 100, 600), 0.72, 0.45, 0.20},
                          {"body", MakeBox(-200, -200, -400, 1200, 1000, 0), 0.30, 0.40, 0.55}};
  std::ostringstream t;
  t << "# Q8 -- STEP round trip (generated by `occt_probe q8`)\n\nOCCT " << OCC_VERSION_COMPLETE << ", " << Rid()
    << ". Three solids, named and coloured, modelled in micrometres, written through `STEPCAFControl_Writer` in the stated unit and "
       "read back through `STEPCAFControl_Reader` into a document whose length unit is the micrometre. Volume error is against the "
       "part as written.\n\n| file | schema | unit written | bytes | unit read | parts | names | colours | worst volume rel err |\n"
       "|---|---|---|---|---|---|---|---|---|\n";
  struct U { const char* tag; UnitsMethods_LengthUnit u; };
  for (auto sch : {DESTEP_Parameters::WriteMode_StepSchema_AP214IS, DESTEP_Parameters::WriteMode_StepSchema_AP242DIS})
    for (U u : {U{"mm", UnitsMethods_LengthUnit_Millimeter}, U{"um", UnitsMethods_LengthUnit_Micron}, U{"mil", UnitsMethods_LengthUnit_Mil}})
    {
      std::string file = std::string("three-parts-") + u.tag + (sch == DESTEP_Parameters::WriteMode_StepSchema_AP242DIS ? "-ap242" : "") + ".step";
      occ::handle<TDocStd_Document> doc = DocOf(parts);
      STEPCAFControl_Writer wr;
      wr.SetNameMode(true);
      wr.SetColorMode(true);
      DESTEP_Parameters prm;
      prm.InitFromStatic();
      prm.WriteUnit   = u.u;
      prm.WriteSchema = sch;
      bool ok = wr.Perform(doc, (d + file).c_str(), prm);
      STEPCAFControl_Reader rd;
      std::string units;
      std::vector<ReadPart> back;
      if (ok && rd.ReadFile((d + file).c_str()) == IFSelect_RetDone) back = ReadStep(rd, 1e-6, &units);
      std::string names, cols;
      double worst = 0;
      for (auto& p : back)
      {
        names += (names.empty() ? "" : ",") + p.name;
        cols += (cols.empty() ? "" : " ") + (p.hasColor ? "(" + F(p.r, 3) + "," + F(p.g, 3) + "," + F(p.b, 3) + ")" : std::string("none"));
        for (auto& q : parts)
          if (q.name == p.name) worst = std::max(worst, std::abs(Volume(p.shape) / Volume(q.shape) - 1));
      }
      t << "| " << file << " | " << (sch == DESTEP_Parameters::WriteMode_StepSchema_AP242DIS ? "AP242" : "AP214") << " | " << u.tag << " | "
        << std::filesystem::file_size(d + file) << " | " << units << " | " << back.size() << " | " << names << " | " << cols << " | "
        << F(worst, 3) << " |\n";
    }
  // a STEP file written by another open-source tool (Gmsh), if present
  std::string foreign = d + "gmsh-written.step";
  if (std::filesystem::exists(foreign))
  {
    STEPCAFControl_Reader rd;
    std::string units;
    std::vector<ReadPart> back;
    if (rd.ReadFile(foreign.c_str()) == IFSelect_RetDone) back = ReadStep(rd, 1e-6, &units);
    t << "\n## A STEP file Gmsh wrote (`gmsh-written.step`, from `gmsh-written.geo`)\n\nunit read: " << units << "; parts: " << back.size()
      << "\n\n| part | name | valid as read | counts | volume (um^3) | ShapeFix (Perform() result; status bits) | valid after fix | volume after fix |\n|---|---|---|---|---|---|---|---|\n";
    int i = 0;
    for (auto& p : back)
    {
      bool v0 = Valid(p.shape);
      double vol0 = Volume(p.shape);
      ShapeFix_Shape fix(p.shape);
      bool changed = fix.Perform(); // DONE4 is set whenever the solid pass runs; this is the "changed" signal
      std::string st = changed ? "changed; " : "unchanged; ";
      if (fix.Status(ShapeExtend_OK)) st += "OK ";
      for (auto [s, n] : {std::pair{ShapeExtend_DONE1, "DONE1"}, {ShapeExtend_DONE2, "DONE2"}, {ShapeExtend_DONE3, "DONE3"},
                          {ShapeExtend_DONE4, "DONE4"}, {ShapeExtend_DONE5, "DONE5"}, {ShapeExtend_FAIL, "FAIL"}})
        if (fix.Status(s)) st += std::string(n) + " ";
      t << "| " << ++i << " | " << (p.name.empty() ? "(none)" : p.name) << " | " << v0 << " | " << Count(p.shape).Str() << " | " << F(vol0, 12)
        << " | " << st << " | " << Valid(fix.Shape()) << " | " << F(Volume(fix.Shape()), 12) << " |\n";
    }
  }
  WriteFile(d + "RESULTS.md", t.str());
  std::printf("q8 wrote %sRESULTS.md and six STEP files\n", d.c_str());
  return 0;
}

// ------------------------------------------------------------------------------------------------
// Q9 -- failure behaviour; one case per process (q9/run.sh records exit status and stderr)
// ------------------------------------------------------------------------------------------------

class Breaker : public Message_ProgressIndicator
{
public:
  explicit Breaker(double ms) : myMs(ms), myT0(Clock::now()) {}
  bool UserBreak() override
  {
    ++myPolls;
    return myMs >= 0 && Ms(myT0) > myMs;
  }
  void Show(const Message_ProgressScope&, const bool) override {}
  int  Polls() const { return myPolls; }

private:
  double            myMs;
  Clock::time_point myT0;
  int               myPolls = 0;
};

static TopoDS_Shape HoleArray(int n, double pitch, double r, NCollection_List<TopoDS_Shape>& tools)
{
  for (int i = 0; i < n; ++i)
    for (int j = 0; j < n; ++j) tools.Append(MakeCyl(pitch * (i + 0.5), pitch * (j + 0.5), -100, r, 700));
  return MakeBox(0, 0, 0, pitch * n, pitch * n, 500);
}

static int CmdQ9(const std::string& c)
{
  std::setvbuf(stdout, nullptr, _IONBF, 0);
  if (c == "fillet-too-big")
  {
    TopoDS_Shape box = MakeBox(0, 0, 0, 1000, 1000, 200);
    BRepFilletAPI_MakeFillet mf(box);
    for (TopExp_Explorer x(box, TopAbs_EDGE); x.More(); x.Next())
    {
      Box6 b = Tight(x.Current());
      if (std::abs(b.z0 - 200) < 1e-9 && std::abs(b.z1 - 200) < 1e-9) { mf.Add(500, TopoDS::Edge(x.Current())); break; }
    }
    try
    {
      mf.Build();
      std::printf("q9 %s: IsDone=%d (no exception)%s\n", c.c_str(), mf.IsDone(), mf.IsDone() ? (" valid=" + std::to_string(Valid(mf.Shape()))).c_str() : "");
    }
    catch (const Standard_Failure& e) { std::printf("q9 %s: Standard_Failure %s: %s\n", c.c_str(), e.ExceptionType(), e.what()); }
    return 0;
  }
  if (c == "self-intersecting")
  {
    BRepBuilderAPI_MakePolygon poly(gp_Pnt(0, 0, 0), gp_Pnt(1000, 1000, 0), gp_Pnt(1000, 0, 0), gp_Pnt(0, 1000, 0), true);
    BRepBuilderAPI_MakeFace face(poly.Wire(), true);
    TopoDS_Shape bow = BRepPrimAPI_MakePrism(face.Face(), gp_Vec(0, 0, 500)).Shape();
    std::printf("q9 %s: input valid=%d counts=%s volume=%s\n", c.c_str(), Valid(bow), Count(bow).Str().c_str(), F(Volume(bow), 8).c_str());
    try
    {
      BRepAlgoAPI_Cut cut(bow, MakeBox(250, 250, 100, 750, 750, 400));
      std::printf("q9 %s: Cut IsDone=%d HasErrors=%d HasWarnings=%d valid=%d counts=%s\n", c.c_str(), cut.IsDone(), cut.HasErrors(),
                  cut.HasWarnings(), cut.IsDone() ? Valid(cut.Shape()) : 0, cut.IsDone() ? Count(cut.Shape()).Str().c_str() : "-");
      if (cut.HasErrors())
      {
        std::ostringstream o;
        cut.DumpErrors(o);
        std::printf("q9 %s: errors: %s\n", c.c_str(), o.str().c_str());
      }
    }
    catch (const Standard_Failure& e) { std::printf("q9 %s: Standard_Failure %s: %s\n", c.c_str(), e.ExceptionType(), e.what()); }
    return 0;
  }
  if (c == "zero-thickness")
  {
    try
    {
      TopoDS_Shape z = MakeBox(0, 0, 0, 1000, 1000, 0);
      std::printf("q9 %s: MakeBox returned, valid=%d counts=%s\n", c.c_str(), Valid(z), Count(z).Str().c_str());
      BRepAlgoAPI_Cut cut(MakeBox(-100, -100, -100, 500, 500, 500), z);
      std::printf("q9 %s: Cut IsDone=%d HasErrors=%d\n", c.c_str(), cut.IsDone(), cut.HasErrors());
    }
    catch (const Standard_Failure& e) { std::printf("q9 %s: Standard_Failure %s: %s\n", c.c_str(), e.ExceptionType(), e.what()); }
    return 0;
  }
  if (c == "tool-misses")
  {
    TopoDS_Shape a = MakeBox(0, 0, 0, 1000, 1000, 1000);
    BRepAlgoAPI_Cut cut(a, MakeBox(5000, 5000, 5000, 6000, 6000, 6000));
    std::printf("q9 %s: IsDone=%d HasErrors=%d HasWarnings=%d valid=%d counts=%s volume=%s\n", c.c_str(), cut.IsDone(), cut.HasErrors(),
                cut.HasWarnings(), Valid(cut.Shape()), Count(cut.Shape()).Str().c_str(), F(Volume(cut.Shape()), 12).c_str());
    return 0;
  }
  if (c == "segv-with-handler" || c == "segv-no-handler")
  {
    if (c == "segv-with-handler") OSD::SetSignal(false);
    int* volatile p = reinterpret_cast<int*>(uintptr_t(c.size() > 1000)); // null, and the compiler cannot prove it
    try
    {
      OCC_CATCH_SIGNALS
      *p = 42;
      std::printf("q9 %s: wrote through null and survived?\n", c.c_str());
    }
    catch (const Standard_Failure& e) { std::printf("q9 %s: caught %s: %s\n", c.c_str(), e.ExceptionType(), e.what()); }
    TopoDS_Shape s = BoredLid(50);
    std::printf("q9 %s: afterwards a boolean + fillet: valid=%d counts=%s\n", c.c_str(), Valid(s), Count(s).Str().c_str());
    return 0;
  }
  if (c == "break-boolean" || c == "break-fillet")
  {
    bool fillet = c == "break-fillet";
    NCollection_List<TopoDS_Shape> tools;
    TopoDS_Shape blank = HoleArray(24, 300, 100, tools);
    TopoDS_Shape holed;
    if (fillet)
    {
      BRepAlgoAPI_Cut pre;
      NCollection_List<TopoDS_Shape> a;
      a.Append(blank);
      pre.SetArguments(a);
      pre.SetTools(tools);
      pre.Build();
      holed = pre.Shape();
    }
    for (double limit : {-1.0, 50.0})
    {
      occ::handle<Breaker> pi = new Breaker(limit);
      auto t0 = Clock::now();
      bool done = false, broke = false;
      try
      {
        if (!fillet)
        {
          BRepAlgoAPI_Cut cut;
          NCollection_List<TopoDS_Shape> a;
          a.Append(blank);
          cut.SetArguments(a);
          cut.SetTools(tools);
          cut.Build(pi->Start());
          done  = cut.IsDone();
          broke = cut.HasError(STANDARD_TYPE(BOPAlgo_AlertUserBreak));
        }
        else
        {
          BRepFilletAPI_MakeFillet mf(holed);
          for (TopExp_Explorer x(holed, TopAbs_EDGE); x.More(); x.Next())
          {
            Box6 b = Tight(x.Current());
            if (std::abs(b.z0 - 500) < 1e-6 && std::abs(b.z1 - 500) < 1e-6 && b.x1 - b.x0 < 250) mf.Add(20, TopoDS::Edge(x.Current()));
          }
          mf.Build(pi->Start());
          done = mf.IsDone();
        }
      }
      catch (const Standard_Failure& e) { std::printf("q9 %s: Standard_Failure %s: %s\n", c.c_str(), e.ExceptionType(), e.what()); }
      std::printf("q9 %s: %s: %s ms, IsDone=%d, user-break alert=%d, UserBreak polled %d times\n", c.c_str(),
                  limit < 0 ? "no break      " : "break at 50 ms", F(Ms(t0), 5).c_str(), done, broke, pi->Polls());
    }
    return 0;
  }
  std::fprintf(stderr, "unknown q9 case %s\n", c.c_str());
  return 2;
}

// ------------------------------------------------------------------------------------------------
// Q10 -- openEMS reads our tessellation
// ------------------------------------------------------------------------------------------------

static void WriteStl(const std::string& path, const Mesh& m)
{
  std::ofstream f(path, std::ios::binary);
  char header[80] = "circuitRF brief-em3d-61 Q10 spike -- binary STL, micrometres";
  f.write(header, 80);
  uint32_t n = uint32_t(m.tris.size());
  f.write(reinterpret_cast<const char*>(&n), 4);
  for (auto& t : m.tris)
  {
    float rec[12] = {0, 0, 0};
    for (int k = 0; k < 3; ++k)
      for (int j = 0; j < 3; ++j) rec[3 + 3 * k + j] = float(m.nodes[t[k]][j]);
    f.write(reinterpret_cast<const char*>(rec), 48);
    uint16_t attr = 0;
    f.write(reinterpret_cast<const char*>(&attr), 2);
  }
}

static void WritePly(const std::string& path, const Mesh& m)
{
  // merged (topological) vertices, ASCII -- the shape F0 Q8 verified
  std::map<int, int> idx;
  std::vector<int> order;
  for (int i = 0; i < int(m.nodes.size()); ++i)
    if (m.merged[i] == i) { idx[i] = int(order.size()); order.push_back(i); }
  std::ofstream f(path);
  f << "ply\nformat ascii 1.0\ncomment circuitRF brief-em3d-61 Q10 spike, micrometres\nelement vertex " << order.size()
    << "\nproperty float x\nproperty float y\nproperty float z\nelement face " << m.tris.size()
    << "\nproperty list uchar int vertex_indices\nend_header\n";
  for (int i : order) f << F(m.nodes[i][0], 9) << " " << F(m.nodes[i][1], 9) << " " << F(m.nodes[i][2], 9) << "\n";
  for (auto& t : m.tris) f << "3 " << idx[m.merged[t[0]]] << " " << idx[m.merged[t[1]]] << " " << idx[m.merged[t[2]]] << "\n";
}

static int CmdQ10()
{
  std::string d = Dir("q10");
  TopoDS_Shape part = BoredLid(50);
  double defl = 5; // um: a tenth of a 50 um FDTD cell
  Mesh m = Tessellate(part, defl, 0.5, false);
  WriteStl(d + "part.stl", m);
  WritePly(d + "part.ply", m);
  std::ostringstream t;
  t << "tessellation of q4's part: linear deflection " << F(defl) << " um (absolute), angular 0.5 rad\n"
    << "  per-face nodes " << m.nodes.size() << ", triangles " << m.tris.size() << "\n"
    << "  merged by edge polygons (topological): " << m.mergedCount << " vertices; edges used once " << m.openTopo << ", used >2 times "
    << m.nonManifoldTopo << ", degenerate triangles " << m.degenerateTopo << " -> " << (m.openTopo == 0 && m.nonManifoldTopo == 0 ? "WATERTIGHT" : "NOT watertight") << "\n"
    << "  merged by exact coordinate: " << m.exactCount << " vertices; edges used once " << m.openExact << ", used >2 times " << m.nonManifoldExact
    << " -> " << (m.openExact == 0 && m.nonManifoldExact == 0 ? "watertight" : "NOT watertight") << "\n";
  WriteFile(d + "mesh.txt", t.str());
  std::printf("q10 %s", t.str().c_str());
  return 0;
}

// ------------------------------------------------------------------------------------------------
// Q12 -- worker-shaped costs (median of 10), for sizing brief 63
// ------------------------------------------------------------------------------------------------

static double Median(std::vector<double> v)
{
  std::sort(v.begin(), v.end());
  return v.size() % 2 ? v[v.size() / 2] : (v[v.size() / 2 - 1] + v[v.size() / 2]) / 2;
}

static int CmdQ12()
{
  std::string d = Dir("q12");
  std::vector<double> tBuild, tDisp, tFdtd, tBrep, tStep;
  Mesh disp, fdtd;
  size_t brepBytes = 0, stepBytes = 0;
  for (int i = 0; i < 10; ++i)
  {
    auto t0 = Clock::now();
    TopoDS_Shape s = BoredLid(50);
    tBuild.push_back(Ms(t0));
    t0 = Clock::now();
    disp = Tessellate(s, 0.001, 20 * M_PI / 180, true); // display: relative 0.1 %, 20 degrees
    tDisp.push_back(Ms(t0));
    t0 = Clock::now();
    fdtd = Tessellate(s, 1.0, 0.5, false); // FDTD: 1 um absolute (a tenth of a 10 um cell)
    tFdtd.push_back(Ms(t0));
    BRepTools::Clean(s);
    t0 = Clock::now();
    std::string b = BrepText(s);
    tBrep.push_back(Ms(t0));
    brepBytes = b.size();
    t0 = Clock::now();
    occ::handle<TDocStd_Document> doc = DocOf({{"lid", s, 0.83, 0.69, 0.22}});
    STEPCAFControl_Writer wr;
    DESTEP_Parameters prm;
    prm.InitFromStatic();
    prm.WriteUnit = UnitsMethods_LengthUnit_Micron;
    std::ostringstream os;
    wr.Transfer(doc, prm);
    wr.WriteStream(os);
    tStep.push_back(Ms(t0));
    stepBytes = os.str().size();
  }
  auto buf = [](const Mesh& m, bool merged) {
    size_t v = merged ? size_t(m.mergedCount) : m.nodes.size();
    return v * 12 + m.tris.size() * 12;
  };
  std::ostringstream t;
  t << "Q12 (occt_probe q12), OCCT " << OCC_VERSION_COMPLETE << ", " << Rid() << ", Release build, median of 10 in one process\n"
    << "  box - cylinder + 50 um fillet (boolean + fillet): " << F(Median(tBuild), 4) << " ms\n"
    << "  display tessellation (relative 0.001, 20 deg):   " << F(Median(tDisp), 4) << " ms, " << disp.tris.size() << " triangles, "
    << disp.nodes.size() << " per-face vertices (" << disp.mergedCount << " merged); float32 xyz + uint32 index buffer: " << buf(disp, false)
    << " bytes per-face, " << buf(disp, true) << " bytes merged\n"
    << "  FDTD tessellation (1 um absolute, 0.5 rad):       " << F(Median(tFdtd), 4) << " ms, " << fdtd.tris.size() << " triangles, "
    << fdtd.nodes.size() << " per-face vertices (" << fdtd.mergedCount << " merged); buffer " << buf(fdtd, false) << " bytes per-face, "
    << buf(fdtd, true) << " bytes merged\n"
    << "  B-rep text (v3, no triangles) write:             " << F(Median(tBrep), 4) << " ms, " << brepBytes << " bytes\n"
    << "  STEP (XCAF, one named part) write to memory:     " << F(Median(tStep), 4) << " ms, " << stepBytes << " bytes\n";
  WriteFile(d + "in-process.txt", t.str());
  std::printf("%s", t.str().c_str());
  return 0;
}

// ------------------------------------------------------------------------------------------------

int main(int argc, char** argv)
{
  std::vector<std::string> a(argv + 1, argv + argc);
  for (size_t i = 0; i + 1 < a.size(); ++i)
    if (a[i] == "--out") { g_out = a[i + 1]; a.erase(a.begin() + i, a.begin() + i + 2); break; }
  if (a.empty())
  {
    std::fprintf(stderr, "usage: occt_probe <selftest|q4|q5|q6|q7|q8|q9 <case>|q10|q12|noop> [--out <dir>]\n");
    return 2;
  }
  // OCCT's default messenger prints transfer statistics to stdout; a worker's stdout is its protocol.
  Message::DefaultMessenger()->RemovePrinters(STANDARD_TYPE(Message_PrinterOStream));
  const std::string& c = a[0];
  if (c == "noop") return CmdNoop();
  if (c == "selftest") return CmdSelftest();
  if (c == "q4") return CmdQ4();
  if (c == "q5") return CmdQ5();
  if (c == "q6") return CmdQ6();
  if (c == "q7") return CmdQ7();
  if (c == "q8") return CmdQ8();
  if (c == "q9") return CmdQ9(a.size() > 1 ? a[1] : "");
  if (c == "q10") return CmdQ10();
  if (c == "q12") return CmdQ12();
  std::fprintf(stderr, "unknown command %s\n", c.c_str());
  return 2;
}
