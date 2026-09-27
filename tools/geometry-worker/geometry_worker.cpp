// ================================================================================================
//  geometry-worker -- circuitRF's geometry kernel, out of process
//
//  Copyright (c) circuitRF contributors. Released under the MIT License (see LICENSE at the root of
//  the circuitRF repository).
//
//  This program uses facilities provided by Open CASCADE Technology (https://dev.opencascade.org),
//  which is distributed under the GNU Lesser General Public License version 2.1 with the Open CASCADE
//  Exception 1.0. It includes OCCT's headers and links OCCT's shared libraries dynamically; nothing
//  of OCCT's is copied into this file or into this directory. See THIRD-PARTY-NOTICES.md.
//
//  ONE SOURCE FILE, like tools/senior-worker. Built by CMakeLists.txt beside it, against the OCCT
//  that occt/recipe.env pins (build.sh builds that into a per-user cache; ensure-built.sh compiles
//  this file against it).
//
//  THE PROTOCOL (brief-em3d-63 R-em3d63-4; README.md is the reference). One FRAME per request on stdin
//  and one per reply on stdout, diagnostics on stderr:
//
//      [uint32 jsonLen][uint32 binLen][jsonLen bytes of UTF-8 JSON][binLen bytes]      little-endian
//
//  The device worker's layout, on purpose, with one difference: the binary part is BYTES, declared by
//  the JSON's "blobs" array in order -- {"name":"vertices","type":"f64","count":3012} -- because a reply
//  carries doubles, 32-bit integers and opaque B-rep/STEP bytes. A refusal is an ordinary reply,
//  {"ok":false,"code":"...","object":"...","detail":"..."}, and the worker keeps running.
//
//      hello        the handshake: protocol, worker version, loaded OCCT, the RID it was built for
//      build        a resolved tree (numbers only, micrometres) -> a held shape, its B-rep, notes
//      tessellate   a held shape -> vertices f64, triangles u32, per-triangle face index u32
//      faces        a held shape -> name, surface kind, tight box, area, min radius per face
//      edges        a held shape -> name, faces, curve kind, length, min radius, polyline per edge
//      export       held shapes -> B-rep, STEP, PLY or STL bytes
//      import-step  STEP bytes (or a path) -> one held shape per part, names, colours, units, healing
//      release      drop held shapes
//      shutdown     answer, then exit 0 ("quit" is the same)
//      box, selftest   brief 62's skeleton checks, kept for tools/CliSmoke
//
//  `geometry-worker --version` prints the worker's version and the OCCT version it loaded, and exits 0
//  (1 when the loaded OCCT is not the one it was built against).
//
//  TEST NODES. With CRF_GEOMETRY_WORKER_TEST=1 in its environment the worker also builds two tree nodes
//  of kind "crash" (it exits at once, answering nothing -- a fault as the client sees one) and "sleep"
//  ("seconds": a build that takes as long as it is told -- what cancellation is tested against). They are
//  nodes of a `build`, not requests of their own, so the client's whole build path -- its cache, its
//  failed-tree record, its restart -- is what the tests exercise. Without the switch both are refused
//  like any unknown kind. brief 63 asked for them in a separate test build; an environment switch keeps
//  one binary, so the worker the tests drive is the one that ships (src/Design/RESOLVED.md).
//
//  FAILURE (brief 61 Q9, docs/design/em-3d-f4b-spike-findings.md "Crash posture"). A C++ exception --
//  Standard_Failure or std::exception -- refuses that one request and the worker carries on. A fault
//  inside the kernel is turned into an exception by OSD::SetSignal (Q9 found the process usable
//  afterwards), but a handler cannot vouch for a heap after a wild write: the worker answers that
//  request with a refusal and then EXITS (status 3), and the client restarts it (40 ms, Q12).
//  Cancellation is always by killing the process -- a fillet never polls a user break.
// ================================================================================================

#include <BRepAdaptor_Curve.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepBndLib.hxx>
#include <BRepBuilderAPI_MakeEdge.hxx>
#include <BRepBuilderAPI_MakeFace.hxx>
#include <BRepBuilderAPI_MakePolygon.hxx>
#include <BRepBuilderAPI_MakeSolid.hxx>
#include <BRepBuilderAPI_MakeVertex.hxx>
#include <BRepBuilderAPI_MakeWire.hxx>
#include <BRepBuilderAPI_Sewing.hxx>
#include <BRepBuilderAPI_Transform.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepFilletAPI_MakeFillet.hxx>
#include <BRepGProp.hxx>
#include <BRepLProp_CLProps.hxx>
#include <BRepLProp_SLProps.hxx>
#include <BRepLib.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRepPrimAPI_MakePrism.hxx>
#include <BRepTools.hxx>
#include <BRep_Builder.hxx>
#include <BRep_Tool.hxx>
#include <Bnd_Box.hxx>
#include <DESTEP_Parameters.hxx>
#include <GCPnts_AbscissaPoint.hxx>
#include <GCPnts_QuasiUniformDeflection.hxx>
#include <GProp_GProps.hxx>
#include <Message.hxx>
#include <Message_Messenger.hxx>
#include <Message_PrinterOStream.hxx>
#include <NCollection_IndexedDataMap.hxx>
#include <NCollection_IndexedMap.hxx>
#include <OSD.hxx>
#include <OSD_Signal.hxx>
#include <Poly_Triangulation.hxx>
#include <Quantity_Color.hxx>
#include <STEPCAFControl_Reader.hxx>
#include <STEPCAFControl_Writer.hxx>
#include <ShapeFix_Shape.hxx>
#include <Standard_ErrorHandler.hxx>
#include <Standard_Failure.hxx>
#include <Standard_Version.hxx>
#include <Standard_VersionInfo.hxx>
#include <TDataStd_Name.hxx>
#include <TDF_Label.hxx>
#include <TDocStd_Document.hxx>
#include <TopExp.hxx>
#include <TopExp_Explorer.hxx>
#include <TopTools_ShapeMapHasher.hxx>
#include <TopoDS.hxx>
#include <TopoDS_Compound.hxx>
#include <XCAFApp_Application.hxx>
#include <XCAFDoc_ColorTool.hxx>
#include <XCAFDoc_DocumentTool.hxx>
#include <XCAFDoc_ShapeTool.hxx>
#include <gp_Ax2.hxx>
#include <gp_Pln.hxx>
#include <gp_Trsf.hxx>

#include <algorithm>
#include <array>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <fstream>
#include <iostream>
#include <map>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#if defined(_WIN32)
  #include <fcntl.h>
  #include <io.h>
  #define crf_dup _dup
  #define crf_dup2 _dup2
  #define crf_fdopen _fdopen
  #define crf_fileno _fileno
#else
  #include <unistd.h>
  #define crf_dup dup
  #define crf_dup2 dup2
  #define crf_fdopen fdopen
  #define crf_fileno fileno
#endif

#ifndef CRF_WORKER_VERSION
  #define CRF_WORKER_VERSION "unknown"
#endif

static const int kProtocol = 1;
static const double kPi = 3.14159265358979323846;

// What this build can do, by feature rather than by toolkit.
static const char* const kModules[] = {"primitives", "booleans", "fillets", "shape-healing", "mesh", "step"};

// The RID this binary was COMPILED for, from the compiler's own predefined macros -- what the client
// compares with its own process architecture (R-em3d63-2b: a wrong-architecture helper fails far from
// its cause).
static const char* CompiledRid()
{
#if defined(_WIN32)
  #if defined(_M_ARM64)
  return "win-arm64";
  #elif defined(_M_X64)
  return "win-x64";
  #else
  return "win-x86";
  #endif
#elif defined(__APPLE__)
  #if defined(__aarch64__) || defined(__arm64__)
  return "osx-arm64";
  #else
  return "osx-x64";
  #endif
#else
  #if defined(__aarch64__)
  return "linux-arm64";
  #elif defined(__x86_64__)
  return "linux-x64";
  #else
  return "linux-unknown";
  #endif
#endif
}

using Shapes    = NCollection_IndexedMap<TopoDS_Shape, TopTools_ShapeMapHasher>;
using Ancestors = NCollection_IndexedDataMap<TopoDS_Shape, NCollection_List<TopoDS_Shape>, TopTools_ShapeMapHasher>;

// The protocol's own stream. Everything else anyone prints -- OCCT included -- lands on stderr: at
// start-up fd 1 is duplicated for the protocol and then pointed at fd 2, so a stray printf deep in a
// translator can never corrupt a response.
static FILE* g_proto = stdout;

static bool g_testOps = false;

// ------------------------------------------------------------------------------------------------
// JSON -- just enough to read a request and write a response
// ------------------------------------------------------------------------------------------------

struct Json
{
  enum Kind { Null, Bool, Number, String, Array, Object } kind = Null;
  bool boolean = false;
  double number = 0;
  std::string text;
  std::vector<Json> items;
  std::vector<std::pair<std::string, Json>> members;

  const Json* Get(const char* key) const
  {
    if (kind != Object) return nullptr;
    for (const auto& m : members)
      if (m.first == key) return &m.second;
    return nullptr;
  }
};

class JsonReader
{
public:
  explicit JsonReader(const std::string& t) : t_(t) {}

  Json Document()
  {
    Json v = Value();
    Space();
    if (i_ != t_.size()) Fail("unexpected text after the value");
    return v;
  }

private:
  const std::string& t_;
  size_t i_ = 0;

  [[noreturn]] void Fail(const char* what) const
  {
    throw std::runtime_error(std::string(what) + " at character " + std::to_string(i_ + 1));
  }

  void Space()
  {
    while (i_ < t_.size() && (t_[i_] == ' ' || t_[i_] == '\t' || t_[i_] == '\r' || t_[i_] == '\n')) ++i_;
  }

  bool Take(const char* word)
  {
    size_t n = std::strlen(word);
    if (t_.compare(i_, n, word) != 0) return false;
    i_ += n;
    return true;
  }

  Json Value()
  {
    Space();
    if (i_ >= t_.size()) Fail("the line ends where a value was expected");
    Json v;
    char c = t_[i_];
    if (c == '{')
    {
      v.kind = Json::Object;
      ++i_;
      Space();
      if (i_ < t_.size() && t_[i_] == '}') { ++i_; return v; }
      for (;;)
      {
        Space();
        if (i_ >= t_.size() || t_[i_] != '"') Fail("expected a member name");
        std::string key = Str();
        Space();
        if (i_ >= t_.size() || t_[i_] != ':') Fail("expected ':'");
        ++i_;
        v.members.emplace_back(std::move(key), Value());
        Space();
        if (i_ < t_.size() && t_[i_] == ',') { ++i_; continue; }
        if (i_ < t_.size() && t_[i_] == '}') { ++i_; return v; }
        Fail("expected ',' or '}'");
      }
    }
    if (c == '[')
    {
      v.kind = Json::Array;
      ++i_;
      Space();
      if (i_ < t_.size() && t_[i_] == ']') { ++i_; return v; }
      for (;;)
      {
        v.items.push_back(Value());
        Space();
        if (i_ < t_.size() && t_[i_] == ',') { ++i_; continue; }
        if (i_ < t_.size() && t_[i_] == ']') { ++i_; return v; }
        Fail("expected ',' or ']'");
      }
    }
    if (c == '"') { v.kind = Json::String; v.text = Str(); return v; }
    if (Take("true"))  { v.kind = Json::Bool; v.boolean = true;  return v; }
    if (Take("false")) { v.kind = Json::Bool; v.boolean = false; return v; }
    if (Take("null"))  { return v; }
    if (c == '-' || (c >= '0' && c <= '9'))
    {
      size_t start = i_;
      if (t_[i_] == '-') ++i_;
      while (i_ < t_.size() && std::strchr("0123456789.eE+-", t_[i_])) ++i_;
      std::string s = t_.substr(start, i_ - start);
      char* end = nullptr;
      v.kind = Json::Number;
      v.number = std::strtod(s.c_str(), &end);
      if (end == nullptr || *end != '\0') Fail("malformed number");
      return v;
    }
    Fail("unexpected character");
  }

  std::string Str()
  {
    ++i_;  // the opening quote
    std::string out;
    while (i_ < t_.size())
    {
      char c = t_[i_++];
      if (c == '"') return out;
      if (c != '\\') { out += c; continue; }
      if (i_ >= t_.size()) break;
      char e = t_[i_++];
      switch (e)
      {
        case '"': out += '"'; break;
        case '\\': out += '\\'; break;
        case '/': out += '/'; break;
        case 'b': out += '\b'; break;
        case 'f': out += '\f'; break;
        case 'n': out += '\n'; break;
        case 'r': out += '\r'; break;
        case 't': out += '\t'; break;
        case 'u':
        {
          if (i_ + 4 > t_.size()) Fail("truncated \\u escape");
          unsigned cp = static_cast<unsigned>(std::stoul(t_.substr(i_, 4), nullptr, 16));
          i_ += 4;
          // Basic Multilingual Plane only; a surrogate pair is passed through as two code units.
          if (cp < 0x80) out += static_cast<char>(cp);
          else if (cp < 0x800) { out += static_cast<char>(0xC0 | (cp >> 6)); out += static_cast<char>(0x80 | (cp & 0x3F)); }
          else
          {
            out += static_cast<char>(0xE0 | (cp >> 12));
            out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (cp & 0x3F));
          }
          break;
        }
        default: Fail("unknown escape");
      }
    }
    Fail("unterminated string");
  }
};

// A JSON writer: values, objects and arrays, commas placed for the caller. Doubles are written with
// 17 significant digits, which round-trips every double; this process never calls setlocale, so the
// decimal point is always '.'.
class JsonOut
{
public:
  JsonOut& Begin()    { Value(); s_ += '{'; first_.push_back(true); return *this; }
  JsonOut& End()      { s_ += '}'; first_.pop_back(); return *this; }
  JsonOut& BeginArr() { Value(); s_ += '['; first_.push_back(true); return *this; }
  JsonOut& EndArr()   { s_ += ']'; first_.pop_back(); return *this; }
  JsonOut& Key(const char* k) { Comma(); Quote(k); s_ += ':'; afterKey_ = true; return *this; }

  JsonOut& Str(const std::string& v) { Value(); Quote(v); return *this; }
  JsonOut& Int(long long v)          { Value(); s_ += std::to_string(v); return *this; }
  JsonOut& Bool(bool v)              { Value(); s_ += v ? "true" : "false"; return *this; }
  JsonOut& Null()                    { Value(); s_ += "null"; return *this; }
  JsonOut& Num(double v)
  {
    Value();
    if (!std::isfinite(v)) { s_ += "null"; return *this; }
    char b[40];
    std::snprintf(b, sizeof b, "%.17g", v);
    s_ += b;
    return *this;
  }

  JsonOut& Str(const char* k, const std::string& v) { return Key(k).Str(v); }
  JsonOut& Int(const char* k, long long v)          { return Key(k).Int(v); }
  JsonOut& Bool(const char* k, bool v)              { return Key(k).Bool(v); }
  JsonOut& Num(const char* k, double v)             { return Key(k).Num(v); }

  const std::string& Text() const { return s_; }

private:
  std::string s_;
  std::vector<bool> first_;
  bool afterKey_ = false;

  void Comma()
  {
    if (first_.empty()) return;
    if (!first_.back()) s_ += ',';
    first_.back() = false;
  }

  void Value()
  {
    if (afterKey_) { afterKey_ = false; return; }
    Comma();
  }

  void Quote(const std::string& v)
  {
    s_ += '"';
    for (unsigned char c : v)
    {
      switch (c)
      {
        case '"': s_ += "\\\""; break;
        case '\\': s_ += "\\\\"; break;
        case '\n': s_ += "\\n"; break;
        case '\r': s_ += "\\r"; break;
        case '\t': s_ += "\\t"; break;
        default:
          if (c < 0x20) { char b[8]; std::snprintf(b, sizeof b, "\\u%04x", c); s_ += b; }
          else s_ += static_cast<char>(c);
      }
    }
    s_ += '"';
  }
};

// ------------------------------------------------------------------------------------------------
// frames (R-em3d63-4a)
// ------------------------------------------------------------------------------------------------

struct Frame
{
  std::string json;
  std::string bin;
};

// A reply under construction: {"ok":true, ...members..., "blobs":[...]} and its binary part.
class Reply
{
public:
  Reply() { j.Begin().Bool("ok", true); }

  JsonOut j;

  void Blob(const char* name, const char* type, size_t count, std::string bytes)
  {
    blobs_.push_back({name, type, count});
    bin_ += bytes;
  }

  Frame Finish()
  {
    if (!blobs_.empty())
    {
      j.Key("blobs").BeginArr();
      for (auto& b : blobs_)
        j.Begin().Str("name", b.name).Str("type", b.type).Int("count", static_cast<long long>(b.count)).End();
      j.EndArr();
    }
    j.End();
    return {j.Text(), std::move(bin_)};
  }

private:
  struct Decl { std::string name, type; size_t count; };
  std::vector<Decl> blobs_;
  std::string bin_;
};

// A refusal: code, the object it concerns (may be empty) and the detail -- OCCT's own text where there
// is one. The client words it (R-em3d63-4d); nothing here is for a person to read first.
struct Refuse
{
  std::string code, object, detail;
};

static Frame Refusal(const Refuse& r)
{
  JsonOut j;
  j.Begin().Bool("ok", false).Str("code", r.code).Str("object", r.object).Str("detail", r.detail).End();
  return {j.Text(), {}};
}

static bool ReadExact(FILE* f, char* p, size_t n)
{
  // fread loops internally, but a pipe may still hand back less than asked: loop until it is all here.
  while (n > 0)
  {
    size_t k = std::fread(p, 1, n, f);
    if (k == 0) return false;
    p += k;
    n -= k;
  }
  return true;
}

static uint32_t Le32(const unsigned char* b)
{
  return uint32_t(b[0]) | (uint32_t(b[1]) << 8) | (uint32_t(b[2]) << 16) | (uint32_t(b[3]) << 24);
}

static void PutLe32(std::string& s, uint32_t v)
{
  for (int i = 0; i < 4; ++i) s += static_cast<char>((v >> (8 * i)) & 0xFF);
}

// False at a clean end of input (the client closed the pipe) or a truncated frame.
static bool ReadFrame(Frame& fr)
{
  unsigned char h[8];
  if (!ReadExact(stdin, reinterpret_cast<char*>(h), 8)) return false;
  uint32_t jl = Le32(h), bl = Le32(h + 4);
  fr.json.assign(jl, '\0');
  fr.bin.assign(bl, '\0');
  if (jl > 0 && !ReadExact(stdin, &fr.json[0], jl)) return false;
  if (bl > 0 && !ReadExact(stdin, &fr.bin[0], bl)) return false;
  return true;
}

static void WriteFrame(const Frame& fr)
{
  std::string h;
  PutLe32(h, static_cast<uint32_t>(fr.json.size()));
  PutLe32(h, static_cast<uint32_t>(fr.bin.size()));
  std::fwrite(h.data(), 1, h.size(), g_proto);
  std::fwrite(fr.json.data(), 1, fr.json.size(), g_proto);
  if (!fr.bin.empty()) std::fwrite(fr.bin.data(), 1, fr.bin.size(), g_proto);
  std::fflush(g_proto);  // every reply, or the client waits on a buffer
}

// A request's blobs, by name, sliced out of its binary part as its "blobs" array declares them.
static std::map<std::string, std::string> RequestBlobs(const Json& req, const std::string& bin)
{
  std::map<std::string, std::string> out;
  const Json* list = req.Get("blobs");
  if (list == nullptr) return out;
  if (list->kind != Json::Array) throw Refuse{"request.malformed", "", "\"blobs\" is not an array"};
  size_t at = 0;
  for (const Json& b : list->items)
  {
    const Json* n = b.Get("name");
    const Json* t = b.Get("type");
    const Json* c = b.Get("count");
    if (n == nullptr || t == nullptr || c == nullptr || n->kind != Json::String || t->kind != Json::String || c->kind != Json::Number)
      throw Refuse{"request.malformed", "", "a blob is declared without a name, a type and a count"};
    size_t unit = t->text == "f64" ? 8 : t->text == "u32" ? 4 : t->text == "bytes" ? 1 : 0;
    if (unit == 0) throw Refuse{"request.malformed", "", "blob type \"" + t->text + "\" is not f64, u32 or bytes"};
    size_t len = static_cast<size_t>(c->number) * unit;
    if (at + len > bin.size()) throw Refuse{"request.malformed", "", "the blobs declare more bytes than the frame carries"};
    out[n->text] = bin.substr(at, len);
    at += len;
  }
  if (at != bin.size()) throw Refuse{"request.malformed", "", "the frame carries bytes no blob declares"};
  return out;
}

static std::string Bytes(const void* p, size_t n) { return std::string(static_cast<const char*>(p), n); }

// ------------------------------------------------------------------------------------------------
// The OCCT this process LOADED, against the one it was BUILT against (R-em3d62-2a)
// ------------------------------------------------------------------------------------------------

static std::string LoadedOcct() { return OCCT_Version_String_Complete(); }

static bool OcctMatches() { return LoadedOcct() == OCC_VERSION_COMPLETE; }

static std::string MismatchSentence()
{
  return "this geometry worker was built against Open CASCADE Technology " OCC_VERSION_COMPLETE
         " but loaded " + LoadedOcct() + "; its libraries are not the ones it shipped with";
}

// ------------------------------------------------------------------------------------------------
// shape helpers
// ------------------------------------------------------------------------------------------------

struct Counts { int solids = 0, faces = 0; };

static Counts Count(const TopoDS_Shape& s)
{
  Counts c;
  if (s.IsNull()) return c;
  for (TopExp_Explorer x(s, TopAbs_SOLID); x.More(); x.Next()) ++c.solids;
  Shapes faces;
  TopExp::MapShapes(s, TopAbs_FACE, faces);
  c.faces = faces.Extent();
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

struct Box6 { double x0, y0, z0, x1, y1, z1; };

static Box6 Tight(const TopoDS_Shape& s)
{
  Bnd_Box b;
  BRepBndLib::AddOptimal(s, b, false, false);
  Box6 r{0, 0, 0, 0, 0, 0};
  if (!b.IsVoid()) b.Get(r.x0, r.y0, r.z0, r.x1, r.y1, r.z1);
  return r;
}

// A deterministic, geometric order -- by tight box, lexicographic (brief 61 Q7: never OCCT's list order).
static bool GeoLess(const TopoDS_Shape& a, const TopoDS_Shape& b)
{
  Box6 p = Tight(a), q = Tight(b);
  std::array<double, 6> u{p.x0, p.y0, p.z0, p.x1, p.y1, p.z1}, v{q.x0, q.y0, q.z0, q.x1, q.y1, q.z1};
  for (int i = 0; i < 6; ++i)
    if (std::abs(u[i] - v[i]) > 1e-6) return u[i] < v[i];
  return false;
}

// The B-rep hand-off: format version 1 (read by every OCCT, and by the one inside Gmsh) and no
// triangles -- written explicitly, because the short overloads write the current version AND
// triangles (brief 61, D12).
static std::string BrepBytes(const TopoDS_Shape& s)
{
  std::ostringstream o;
  BRepTools::Write(s, o, false, false, TopTools_FormatVersion_VERSION_1);
  return o.str();
}

// ------------------------------------------------------------------------------------------------
// shapes the worker holds, by the handle the client gave them (the tree's hash)
// ------------------------------------------------------------------------------------------------

struct Held
{
  TopoDS_Shape shape;
  std::vector<std::string> faceNames;  // by face index, TopExp::MapShapes order
};

static std::map<std::string, Held> g_shapes;

static Held& Find(const Json& req, const char* key = "shape")
{
  const Json* h = req.Get(key);
  if (h == nullptr || h->kind != Json::String) throw Refuse{"request.malformed", "", std::string("expected \"") + key + "\": a shape handle"};
  auto it = g_shapes.find(h->text);
  if (it == g_shapes.end()) throw Refuse{"shape.unknown", "", "this worker holds no shape '" + h->text + "'"};
  return it->second;
}

// ------------------------------------------------------------------------------------------------
// reading a tree (R-em3d63-5): numbers only, micrometres, and circuitRF's face names
// ------------------------------------------------------------------------------------------------

struct Named
{
  TopoDS_Shape shape;
  std::vector<std::pair<TopoDS_Shape, std::string>> faces;
};

struct NodeReader
{
  const Json& node;
  std::string name;

  [[noreturn]] void Bad(const std::string& what) const { throw Refuse{"tree.invalid", name, what}; }

  const Json& Member(const char* key) const
  {
    const Json* v = node.Get(key);
    if (v == nullptr) Bad(std::string("the node has no \"") + key + "\"");
    return *v;
  }

  static bool IsNum(const Json& v) { return v.kind == Json::Number && std::isfinite(v.number); }

  double Num(const char* key) const
  {
    const Json& v = Member(key);
    if (!IsNum(v)) Bad(std::string("\"") + key + "\" is not a finite number");
    return v.number;
  }

  gp_XYZ Xyz(const Json& v, const char* what) const
  {
    if (v.kind != Json::Array || v.items.size() != 3 || !IsNum(v.items[0]) || !IsNum(v.items[1]) || !IsNum(v.items[2]))
      Bad(std::string(what) + " is not three finite numbers");
    return gp_XYZ(v.items[0].number, v.items[1].number, v.items[2].number);
  }

  gp_XYZ Xyz(const char* key) const { return Xyz(Member(key), key); }

  std::vector<gp_Pnt> Points(const Json& v, const char* what) const
  {
    if (v.kind != Json::Array) Bad(std::string(what) + " is not an array of points");
    std::vector<gp_Pnt> p;
    for (const Json& q : v.items) p.emplace_back(Xyz(q, what));
    return p;
  }

  std::vector<std::string> FaceNames() const
  {
    const Json& v = Member("faces");
    if (v.kind != Json::Array) Bad("\"faces\" is not an array of names");
    std::vector<std::string> out;
    for (const Json& n : v.items)
    {
      if (n.kind != Json::String) Bad("\"faces\" holds something that is not a name");
      out.push_back(n.text);
    }
    return out;
  }
};

// Newell's normal of a closed loop: robust for any planar polygon, convex or not.
static gp_XYZ Newell(const std::vector<gp_Pnt>& p)
{
  gp_XYZ n(0, 0, 0);
  for (size_t i = 0; i < p.size(); ++i)
  {
    const gp_Pnt& a = p[i];
    const gp_Pnt& b = p[(i + 1) % p.size()];
    n += gp_XYZ((a.Y() - b.Y()) * (a.Z() + b.Z()), (a.Z() - b.Z()) * (a.X() + b.X()), (a.X() - b.X()) * (a.Y() + b.Y()));
  }
  return n;
}

// A closed polygon as a wire, with its edges in order: edge k runs from point k to point k+1.
static TopoDS_Wire Loop(const std::vector<gp_Pnt>& p, std::vector<TopoDS_Edge>* edges, const NodeReader& r)
{
  if (p.size() < 3) r.Bad("a loop has fewer than three points");
  BRepBuilderAPI_MakePolygon mp;
  for (size_t i = 0; i < p.size(); ++i)
  {
    mp.Add(p[i]);
    if (i > 0)
    {
      if (!mp.Added()) r.Bad("a loop repeats a point, so one of its edges has no length");
      if (edges) edges->push_back(mp.Edge());
    }
  }
  mp.Close();
  if (!mp.IsDone()) r.Bad("a loop does not make a closed polygon");
  if (edges) edges->push_back(mp.Edge());
  return mp.Wire();
}

// A planar face from an outer loop and holes, oriented along the outer loop's own normal.
static TopoDS_Face PlanarFace(const std::vector<gp_Pnt>& outer, const std::vector<std::vector<gp_Pnt>>& holes,
                              std::vector<TopoDS_Edge>* outerEdges, std::vector<std::vector<TopoDS_Edge>>* holeEdges,
                              const NodeReader& r)
{
  gp_XYZ n = Newell(outer);
  if (n.Modulus() <= 0) r.Bad("a loop encloses no area");
  gp_Pln pln(outer[0], gp_Dir(n));
  TopoDS_Wire ow = Loop(outer, outerEdges, r);
  BRepBuilderAPI_MakeFace mf(pln, ow, true);
  if (!mf.IsDone()) r.Bad("a loop does not bound a planar face");
  for (const auto& h : holes)
  {
    std::vector<TopoDS_Edge> he;
    TopoDS_Wire hw = Loop(h, holeEdges ? &he : nullptr, r);
    // A hole runs against its face: reverse one that was drawn the same way round as the outline.
    if (Newell(h).Dot(n) > 0) hw = TopoDS::Wire(hw.Reversed());
    mf.Add(hw);
    if (holeEdges) holeEdges->push_back(he);
  }
  if (!mf.IsDone()) r.Bad("a hole does not fit its face");
  return mf.Face();
}

static TopoDS_Shape OrientedSolid(const TopoDS_Shape& s)
{
  for (TopExp_Explorer x(s, TopAbs_SOLID); x.More(); x.Next())
  {
    TopoDS_Solid so = TopoDS::Solid(x.Current());
    BRepLib::OrientClosedSolid(so);
    return so;
  }
  return s;
}

static Named BuildBox(const NodeReader& r, const std::vector<std::string>& names)
{
  if (names.size() != 6) r.Bad("a box names six faces");
  gp_XYZ lo = r.Xyz("min"), size = r.Xyz("size");
  gp_XYZ hi = lo + size;
  gp_Pnt a(std::min(lo.X(), hi.X()), std::min(lo.Y(), hi.Y()), std::min(lo.Z(), hi.Z()));
  gp_Pnt b(std::max(lo.X(), hi.X()), std::max(lo.Y(), hi.Y()), std::max(lo.Z(), hi.Z()));
  TopoDS_Shape s = BRepPrimAPI_MakeBox(a, b).Shape();
  Named n{s, {}};
  // By each face's plane: its normal picks the axis, its position against the middle picks min or max.
  double mid[3] = {(a.X() + b.X()) / 2, (a.Y() + b.Y()) / 2, (a.Z() + b.Z()) / 2};
  for (TopExp_Explorer x(s, TopAbs_FACE); x.More(); x.Next())
  {
    gp_Pln pl = BRepAdaptor_Surface(TopoDS::Face(x.Current())).Plane();
    gp_Dir d = pl.Axis().Direction();
    int axis = std::abs(d.X()) > 0.5 ? 0 : std::abs(d.Y()) > 0.5 ? 1 : 2;
    double at = pl.Location().Coord(axis + 1);
    n.faces.push_back({x.Current(), names[2 * axis + (at > mid[axis] ? 1 : 0)]});
  }
  return n;
}

static Named BuildCylinder(const NodeReader& r, const std::vector<std::string>& names)
{
  if (names.size() != 3) r.Bad("a cylinder names three faces: bottom, top and side");
  gp_Pnt base(r.Xyz("base"));
  gp_XYZ axis = r.Xyz("axis");
  double length = r.Num("length"), radius = r.Num("radius");
  if (axis.Modulus() <= 0) r.Bad("the axis has no direction");
  if (radius <= 0) r.Bad("the radius is not positive");
  if (length == 0) r.Bad("the length is zero");
  gp_Dir dir(length < 0 ? axis.Reversed() : axis);
  TopoDS_Shape s = BRepPrimAPI_MakeCylinder(gp_Ax2(base, dir), radius, std::abs(length)).Shape();
  Named n{s, {}};
  for (TopExp_Explorer x(s, TopAbs_FACE); x.More(); x.Next())
  {
    const TopoDS_Face& f = TopoDS::Face(x.Current());
    std::string nm = names[2];
    if (BRepAdaptor_Surface(f).GetType() == GeomAbs_Plane)
    {
      // bottom is the cap at Base, whichever way the length runs
      GProp_GProps p;
      BRepGProp::SurfaceProperties(f, p);
      nm = gp_Vec(base, p.CentreOfMass()).Dot(gp_Vec(dir)) > std::abs(length) / 2 ? names[1] : names[0];
    }
    n.faces.push_back({f, nm});
  }
  return n;
}

static Named BuildPrism(const NodeReader& r, const std::vector<std::string>& names)
{
  std::vector<gp_Pnt> outline = r.Points(r.Member("outline"), "outline");
  std::vector<std::vector<gp_Pnt>> holes;
  if (const Json* hs = r.node.Get("holes"))
  {
    if (hs->kind != Json::Array) r.Bad("\"holes\" is not an array of loops");
    for (const Json& h : hs->items) holes.push_back(r.Points(h, "a hole"));
  }
  size_t expected = 2 + outline.size();
  for (auto& h : holes) expected += h.size();
  if (names.size() != expected) r.Bad("a prism names bottom, top, then one face per outline and hole edge");
  gp_Vec v(r.Xyz("extrude"));
  if (v.Magnitude() <= 0) r.Bad("the extrusion has no length");

  std::vector<TopoDS_Edge> oe;
  std::vector<std::vector<TopoDS_Edge>> he;
  TopoDS_Face base = PlanarFace(outline, holes, &oe, &he, r);
  BRepPrimAPI_MakePrism mp(base, v, true);
  if (!mp.IsDone()) r.Bad("the outline does not extrude");
  Named n{OrientedSolid(mp.Shape()), {}};
  n.faces.push_back({mp.FirstShape(), names[0]});
  n.faces.push_back({mp.LastShape(), names[1]});
  size_t k = 2;
  auto side = [&](const TopoDS_Edge& e) {
    const NCollection_List<TopoDS_Shape>& g = mp.Generated(e);
    if (!g.IsEmpty()) n.faces.push_back({g.First(), names[k]});
    ++k;
  };
  for (const auto& e : oe) side(e);
  for (const auto& list : he)
    for (const auto& e : list) side(e);
  return n;
}

static Named BuildPolyhedron(const NodeReader& r, const std::vector<std::string>& names)
{
  std::vector<gp_Pnt> v = r.Points(r.Member("vertices"), "vertices");
  const Json& loops = r.Member("loops");
  if (loops.kind != Json::Array) r.Bad("\"loops\" is not an array");
  if (names.size() != loops.items.size()) r.Bad("a polyhedron names one face per loop");
  auto pts = [&](const Json& idx) {
    if (idx.kind != Json::Array) r.Bad("a loop is not an array of vertex indices");
    std::vector<gp_Pnt> p;
    for (const Json& i : idx.items)
    {
      if (i.kind != Json::Number || i.number < 0 || i.number >= double(v.size()) || i.number != std::floor(i.number))
        r.Bad("a loop names a vertex the polyhedron does not have");
      p.push_back(v[static_cast<size_t>(i.number)]);
    }
    return p;
  };

  std::vector<TopoDS_Face> faces;
  BRepBuilderAPI_Sewing sew(1e-4);
  for (const Json& l : loops.items)
  {
    const Json* outer = l.Get("outer");
    if (l.kind != Json::Object || outer == nullptr) r.Bad("a loop is not {\"outer\": [...], \"holes\": [[...]]}");
    std::vector<std::vector<gp_Pnt>> holes;
    if (const Json* hs = l.Get("holes"))
    {
      if (hs->kind != Json::Array) r.Bad("a loop's \"holes\" is not an array");
      for (const Json& h : hs->items) holes.push_back(pts(h));
    }
    TopoDS_Face f = PlanarFace(pts(*outer), holes, nullptr, nullptr, r);
    faces.push_back(f);
    sew.Add(f);
  }
  sew.Perform();
  TopoDS_Shape sewed = sew.SewedShape();
  TopoDS_Shell shell;
  int shells = 0;
  for (TopExp_Explorer x(sewed, TopAbs_SHELL); x.More(); x.Next()) { shell = TopoDS::Shell(x.Current()); ++shells; }
  if (shells != 1) r.Bad("the faces do not close into one shell");
  BRepBuilderAPI_MakeSolid ms(shell);
  if (!ms.IsDone()) r.Bad("the shell does not bound a solid");
  Named n{OrientedSolid(ms.Solid()), {}};
  for (size_t i = 0; i < faces.size(); ++i)
    n.faces.push_back({sew.IsModified(faces[i]) ? sew.Modified(faces[i]) : faces[i], names[i]});
  return n;
}

// One node: its primitive in its own frame, named, then carried by its transform.
static Named BuildNode(const Json& node)
{
  if (node.kind != Json::Object) throw Refuse{"tree.invalid", "", "a tree node is not an object"};
  NodeReader r{node, ""};
  if (const Json* nm = node.Get("name"); nm && nm->kind == Json::String) r.name = nm->text;
  const Json& kind = r.Member("kind");
  if (kind.kind != Json::String) r.Bad("\"kind\" is not a string");

  if (g_testOps && kind.text == "crash")
  {
    std::fprintf(stderr, "geometry-worker: exiting on a test crash node\n");
    std::fflush(stderr);
    std::_Exit(70);
  }
  if (g_testOps && kind.text == "sleep")
  {
    std::this_thread::sleep_for(std::chrono::duration<double>(r.Num("seconds")));
    return BuildNode(r.Member("then"));
  }

  std::vector<std::string> names = r.FaceNames();
  Named n;
  if (kind.text == "box") n = BuildBox(r, names);
  else if (kind.text == "cylinder") n = BuildCylinder(r, names);
  else if (kind.text == "prism") n = BuildPrism(r, names);
  else if (kind.text == "polyhedron") n = BuildPolyhedron(r, names);
  else r.Bad("this worker cannot build a \"" + kind.text + "\"");

  if (const Json* t = node.Get("transform"))
  {
    if (t->kind != Json::Array || t->items.size() != 12) r.Bad("\"transform\" is not twelve numbers (a 3 x 4 matrix, rows)");
    double m[12];
    for (int i = 0; i < 12; ++i)
    {
      if (!NodeReader::IsNum(t->items[i])) r.Bad("\"transform\" holds something that is not a finite number");
      m[i] = t->items[i].number;
    }
    static const double id[12] = {1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0};
    if (!std::equal(m, m + 12, id))
    {
      gp_Trsf tr;
      tr.SetValues(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11]);
      BRepBuilderAPI_Transform xf(n.shape, tr, true);
      if (!xf.IsDone()) r.Bad("the transform could not be applied");
      Named moved{xf.Shape(), {}};
      for (auto& [f, nm] : n.faces) moved.faces.push_back({xf.ModifiedShape(f), nm});
      n = moved;
    }
  }
  return n;
}

static Held Hold(const Named& n)
{
  Held h{n.shape, {}};
  Shapes faces;
  TopExp::MapShapes(n.shape, TopAbs_FACE, faces);
  h.faceNames.assign(faces.Extent(), "");
  for (auto& [f, nm] : n.faces)
  {
    int i = faces.FindIndex(f);
    if (i > 0) h.faceNames[i - 1] = nm;
  }
  return h;
}

// ------------------------------------------------------------------------------------------------
// the requests
// ------------------------------------------------------------------------------------------------

static Frame OpHello(const Json& req)
{
  if (const Json* p = req.Get("protocol"))
    if (p->kind != Json::Number || p->number != kProtocol)
      throw Refuse{"protocol.mismatch", "", "this geometry worker speaks protocol " + std::to_string(kProtocol) + " only"};
  Reply r;
  r.j.Str("worker", CRF_WORKER_VERSION).Str("occt", LoadedOcct()).Int("protocol", kProtocol).Str("rid", CompiledRid());
  r.j.Key("modules").BeginArr();
  for (const char* m : kModules) r.j.Str(m);
  r.j.EndArr();
  r.j.Bool("test_ops", g_testOps);
  return r.Finish();
}

static Frame OpBuild(const Json& req)
{
  const Json* handle = req.Get("shape");
  const Json* tree = req.Get("tree");
  if (handle == nullptr || handle->kind != Json::String || handle->text.empty())
    throw Refuse{"request.malformed", "", "build needs \"shape\": the handle to hold the result under"};
  if (tree == nullptr) throw Refuse{"request.malformed", "", "build needs \"tree\""};
  const Json* root = tree->Get("root");
  if (root == nullptr) throw Refuse{"tree.invalid", "", "the tree has no \"root\""};
  std::string object;
  if (const Json* nm = root->Get("name"); nm && nm->kind == Json::String) object = nm->text;
  if (const Json* o = req.Get("options"))
  {
    if (const Json* fz = o->Get("fuzzy"); fz && (fz->kind != Json::Number || fz->number < 0))
      throw Refuse{"request.malformed", object, "\"fuzzy\" is not a non-negative number"};
  }

  Named n;
  try
  {
    n = BuildNode(*root);
  }
  catch (const Standard_Failure& e)
  {
    throw Refuse{"build.failed", object, std::string(e.ExceptionType()) + (e.what() && *e.what() ? std::string(": ") + e.what() : "")};
  }
  if (!Valid(n.shape)) throw Refuse{"build.invalid-result", object, "the result does not pass the kernel's validity check"};

  Held h = Hold(n);
  std::vector<std::string> notes;
  int unnamed = 0;
  for (auto& f : h.faceNames) if (f.empty()) ++unnamed;
  if (unnamed > 0) notes.push_back(std::to_string(unnamed) + " face(s) of the result carry no name");

  // The hand-off bytes come straight from the result, before any other request touches the shape: a
  // later STEP export clears flag bits a validity check set (brief 61, D12's caveat).
  std::string brep = BrepBytes(h.shape);
  Counts c = Count(h.shape);
  double volume = Volume(h.shape);
  g_shapes[handle->text] = h;

  Reply r;
  r.j.Str("shape", handle->text).Str("object", object).Bool("valid", true).Int("solids", c.solids).Int("faces", c.faces)
     .Num("volume_um3", volume);
  r.j.Key("notes").BeginArr();
  for (auto& s : notes) r.j.Str(s);
  r.j.EndArr();
  r.Blob("brep", "bytes", brep.size(), brep);
  return r.Finish();
}

struct Mesh
{
  std::vector<double> xyz;          // 3 per node
  std::vector<uint32_t> tris;       // 3 per triangle, node indices
  std::vector<uint32_t> faceOfTri;  // per triangle, the face index (MapShapes order, 0-based)
};

static Mesh Tessellate(const TopoDS_Shape& s, double lin, double ang)
{
  BRepTools::Clean(s);
  BRepMesh_IncrementalMesh(s, lin, false, ang, false);
  Mesh m;
  Shapes faces;
  TopExp::MapShapes(s, TopAbs_FACE, faces);
  for (int fi = 1; fi <= faces.Extent(); ++fi)
  {
    TopoDS_Face f = TopoDS::Face(faces(fi));
    TopLoc_Location loc;
    occ::handle<Poly_Triangulation> tri = BRep_Tool::Triangulation(f, loc);
    if (tri.IsNull()) continue;
    uint32_t base = static_cast<uint32_t>(m.xyz.size() / 3);
    gp_Trsf tr = loc.Transformation();
    for (int i = 1; i <= tri->NbNodes(); ++i)
    {
      gp_Pnt p = tri->Node(i).Transformed(tr);
      m.xyz.push_back(p.X());
      m.xyz.push_back(p.Y());
      m.xyz.push_back(p.Z());
    }
    bool rev = f.Orientation() == TopAbs_REVERSED;
    for (int i = 1; i <= tri->NbTriangles(); ++i)
    {
      int a, b, c;
      tri->Triangle(i).Get(a, b, c);
      if (rev) std::swap(b, c);
      m.tris.push_back(base + uint32_t(a - 1));
      m.tris.push_back(base + uint32_t(b - 1));
      m.tris.push_back(base + uint32_t(c - 1));
      m.faceOfTri.push_back(uint32_t(fi - 1));
    }
  }
  return m;
}

static void Deflections(const Json& req, const char* linKey, double& lin, double& ang)
{
  const Json* l = req.Get(linKey);
  const Json* a = req.Get("angular_rad");
  if (l == nullptr || l->kind != Json::Number || !(l->number > 0))
    throw Refuse{"request.malformed", "", std::string("\"") + linKey + "\" is not a positive number of micrometres"};
  lin = l->number;
  ang = 0.5;
  if (a != nullptr)
  {
    if (a->kind != Json::Number || !(a->number > 0)) throw Refuse{"request.malformed", "", "\"angular_rad\" is not a positive angle"};
    ang = a->number;
  }
}

static Frame OpTessellate(const Json& req)
{
  Held& h = Find(req);
  double lin, ang;
  Deflections(req, "linear_um", lin, ang);
  Mesh m = Tessellate(h.shape, lin, ang);
  Reply r;
  r.j.Int("vertices", static_cast<long long>(m.xyz.size() / 3)).Int("triangles", static_cast<long long>(m.faceOfTri.size()))
     .Int("faces", static_cast<long long>(h.faceNames.size()));
  r.Blob("vertices", "f64", m.xyz.size(), Bytes(m.xyz.data(), m.xyz.size() * 8));
  r.Blob("tris", "u32", m.tris.size(), Bytes(m.tris.data(), m.tris.size() * 4));
  r.Blob("face", "u32", m.faceOfTri.size(), Bytes(m.faceOfTri.data(), m.faceOfTri.size() * 4));
  return r.Finish();
}

static const char* SurfaceKind(GeomAbs_SurfaceType t)
{
  switch (t)
  {
    case GeomAbs_Plane: return "plane";
    case GeomAbs_Cylinder: return "cylinder";
    case GeomAbs_Cone: return "cone";
    case GeomAbs_Sphere: return "sphere";
    case GeomAbs_Torus: return "torus";
    case GeomAbs_BSplineSurface:
    case GeomAbs_BezierSurface: return "bspline";
    default: return "other";
  }
}

// The smallest radius of curvature a face has: 0 for a plane, the radius for the analytic kinds, and
// otherwise sampled on a 9 x 9 grid of its parameter range.
static double FaceMinRadius(const TopoDS_Face& f)
{
  BRepAdaptor_Surface a(f);
  switch (a.GetType())
  {
    case GeomAbs_Plane: return 0;
    case GeomAbs_Cylinder: return a.Cylinder().Radius();
    case GeomAbs_Sphere: return a.Sphere().Radius();
    case GeomAbs_Torus: return a.Torus().MinorRadius();
    default: break;
  }
  double kmax = 0;
  const int n = 9;
  BRepLProp_SLProps p(a, 2, 1e-9);
  for (int i = 0; i < n; ++i)
    for (int j = 0; j < n; ++j)
    {
      double u = a.FirstUParameter() + (a.LastUParameter() - a.FirstUParameter()) * i / (n - 1);
      double v = a.FirstVParameter() + (a.LastVParameter() - a.FirstVParameter()) * j / (n - 1);
      p.SetParameters(u, v);
      if (p.IsCurvatureDefined())
        kmax = std::max({kmax, std::abs(p.MaxCurvature()), std::abs(p.MinCurvature())});
    }
  return kmax > 1e-12 ? 1 / kmax : 0;
}

static Frame OpFaces(const Json& req)
{
  Held& h = Find(req);
  Shapes faces;
  TopExp::MapShapes(h.shape, TopAbs_FACE, faces);
  Reply r;
  r.j.Key("faces").BeginArr();
  for (int i = 1; i <= faces.Extent(); ++i)
  {
    TopoDS_Face f = TopoDS::Face(faces(i));
    Box6 b = Tight(f);
    r.j.Begin()
       .Str("name", h.faceNames[i - 1])
       .Str("kind", SurfaceKind(BRepAdaptor_Surface(f).GetType()));
    r.j.Key("box").BeginArr().Num(b.x0).Num(b.y0).Num(b.z0).Num(b.x1).Num(b.y1).Num(b.z1).EndArr();
    r.j.Num("area", Area(f)).Num("min_radius", FaceMinRadius(f)).End();
  }
  r.j.EndArr();
  return r.Finish();
}

static const char* CurveKind(GeomAbs_CurveType t)
{
  switch (t)
  {
    case GeomAbs_Line: return "line";
    case GeomAbs_Circle: return "circle";
    case GeomAbs_Ellipse: return "ellipse";
    case GeomAbs_BSplineCurve:
    case GeomAbs_BezierCurve: return "bspline";
    default: return "other";
  }
}

static double EdgeMinRadius(const BRepAdaptor_Curve& c)
{
  switch (c.GetType())
  {
    case GeomAbs_Line: return 0;
    case GeomAbs_Circle: return c.Circle().Radius();
    default: break;
  }
  double kmax = 0;
  const int n = 33;
  BRepLProp_CLProps p(c, 2, 1e-9);
  for (int i = 0; i < n; ++i)
  {
    p.SetParameter(c.FirstParameter() + (c.LastParameter() - c.FirstParameter()) * i / (n - 1));
    if (p.IsTangentDefined()) kmax = std::max(kmax, std::abs(p.Curvature()));
  }
  return kmax > 1e-12 ? 1 / kmax : 0;
}

// Edges are named by the two faces they separate, sorted and joined by '|'; where a pair bounds more
// than one edge a third field numbers them in geometric order (overview section 1g, as brief 61 Q7
// corrected it). A seam -- a face meeting itself -- and a degenerate edge are not feature edges and
// are left out.
static Frame OpEdges(const Json& req)
{
  Held& h = Find(req);
  const Json* d = req.Get("deflection_um");
  if (d == nullptr || d->kind != Json::Number || !(d->number > 0))
    throw Refuse{"request.malformed", "", "\"deflection_um\" is not a positive number of micrometres"};

  Shapes faces;
  TopExp::MapShapes(h.shape, TopAbs_FACE, faces);
  Ancestors anc;
  TopExp::MapShapesAndAncestors(h.shape, TopAbs_EDGE, TopAbs_FACE, anc);
  std::map<std::string, std::vector<std::pair<TopoDS_Edge, std::array<std::string, 2>>>> byPair;
  for (int i = 1; i <= anc.Extent(); ++i)
  {
    TopoDS_Edge e = TopoDS::Edge(anc.FindKey(i));
    if (BRep_Tool::Degenerated(e)) continue;
    Shapes distinct;
    for (const TopoDS_Shape& f : anc(i)) distinct.Add(f);
    if (distinct.Extent() != 2) continue;  // a seam (one face) or a non-manifold edge
    std::array<std::string, 2> fn{h.faceNames[faces.FindIndex(distinct(1)) - 1], h.faceNames[faces.FindIndex(distinct(2)) - 1]};
    std::sort(fn.begin(), fn.end());
    byPair[fn[0] + "|" + fn[1]].push_back({e, fn});
  }

  Reply r;
  std::vector<double> poly;
  r.j.Key("edges").BeginArr();
  for (auto& [key, list] : byPair)
  {
    std::stable_sort(list.begin(), list.end(), [](const auto& a, const auto& b) { return GeoLess(a.first, b.first); });
    for (size_t k = 0; k < list.size(); ++k)
    {
      const TopoDS_Edge& e = list[k].first;
      BRepAdaptor_Curve c(e);
      size_t before = poly.size();
      GCPnts_QuasiUniformDeflection q(c, d->number);
      if (q.IsDone() && q.NbPoints() >= 2)
        for (int i = 1; i <= q.NbPoints(); ++i)
        {
          gp_Pnt p = q.Value(i);
          poly.insert(poly.end(), {p.X(), p.Y(), p.Z()});
        }
      else
        for (double t : {c.FirstParameter(), c.LastParameter()})
        {
          gp_Pnt p = c.Value(t);
          poly.insert(poly.end(), {p.X(), p.Y(), p.Z()});
        }
      r.j.Begin().Str("name", list.size() > 1 ? key + "|" + std::to_string(k + 1) : key);
      r.j.Key("faces").BeginArr().Str(list[k].second[0]).Str(list[k].second[1]).EndArr();
      r.j.Str("kind", CurveKind(c.GetType()))
         .Num("length", GCPnts_AbscissaPoint::Length(c))
         .Num("min_radius", EdgeMinRadius(c))
         .Int("points", static_cast<long long>((poly.size() - before) / 3))
         .End();
    }
  }
  r.j.EndArr();
  r.Blob("polylines", "f64", poly.size(), Bytes(poly.data(), poly.size() * 8));
  return r.Finish();
}

static occ::handle<TDocStd_Document> NewXcafDocument()
{
  occ::handle<TDocStd_Document> doc;
  XCAFApp_Application::GetApplication()->NewDocument("MDTV-XCAF", doc);
  XCAFDoc_DocumentTool::SetLengthUnit(doc, 1e-6);
  return doc;
}

// Micrometres per unit, for the units an export may be written in.
static double MicronsPer(const std::string& u, UnitsMethods_LengthUnit* step)
{
  struct U { const char* name; double um; UnitsMethods_LengthUnit step; };
  static const U table[] = {
    {"um", 1, UnitsMethods_LengthUnit_Micron}, {"mm", 1000, UnitsMethods_LengthUnit_Millimeter},
    {"mil", 25.4, UnitsMethods_LengthUnit_Mil}, {"in", 25400, UnitsMethods_LengthUnit_Inch},
    {"m", 1e6, UnitsMethods_LengthUnit_Meter}};
  for (const U& x : table)
    if (u == x.name) { if (step) *step = x.step; return x.um; }
  throw Refuse{"request.malformed", "", "\"units\" is not one of um, mm, mil, in, m"};
}

static Frame OpExport(const Json& req)
{
  const Json* list = req.Get("shapes");
  const Json* format = req.Get("format");
  if (list == nullptr || list->kind != Json::Array || list->items.empty())
    throw Refuse{"request.malformed", "", "export needs \"shapes\": one or more handles"};
  if (format == nullptr || format->kind != Json::String) throw Refuse{"request.malformed", "", "export needs \"format\""};
  std::vector<TopoDS_Shape> shapes;
  for (const Json& h : list->items)
  {
    if (h.kind != Json::String) throw Refuse{"request.malformed", "", "\"shapes\" holds something that is not a handle"};
    auto it = g_shapes.find(h.text);
    if (it == g_shapes.end()) throw Refuse{"shape.unknown", "", "this worker holds no shape '" + h.text + "'"};
    shapes.push_back(it->second.shape);
  }
  std::string units = "um";
  if (const Json* u = req.Get("units"); u && u->kind == Json::String) units = u->text;
  UnitsMethods_LengthUnit stepUnit = UnitsMethods_LengthUnit_Micron;
  double per = MicronsPer(units, &stepUnit);
  const Json* names = req.Get("names");
  const Json* colours = req.Get("colours");

  std::string data;
  const std::string& f = format->text;
  if (f == "brep")
  {
    if (shapes.size() == 1) data = BrepBytes(shapes[0]);
    else
    {
      TopoDS_Compound c;
      BRep_Builder b;
      b.MakeCompound(c);
      for (auto& s : shapes) b.Add(c, s);
      data = BrepBytes(c);
    }
  }
  else if (f == "step")
  {
    occ::handle<TDocStd_Document> doc = NewXcafDocument();
    occ::handle<XCAFDoc_ShapeTool> st = XCAFDoc_DocumentTool::ShapeTool(doc->Main());
    occ::handle<XCAFDoc_ColorTool> ct = XCAFDoc_DocumentTool::ColorTool(doc->Main());
    for (size_t i = 0; i < shapes.size(); ++i)
    {
      TDF_Label l = st->AddShape(shapes[i], false);
      if (names && names->kind == Json::Array && i < names->items.size() && names->items[i].kind == Json::String)
        TDataStd_Name::Set(l, TCollection_ExtendedString(names->items[i].text.c_str(), true));
      if (colours && colours->kind == Json::Array && i < colours->items.size())
      {
        const Json& c = colours->items[i];
        if (c.kind == Json::Array && c.items.size() == 3)
          ct->SetColor(l, Quantity_Color(c.items[0].number, c.items[1].number, c.items[2].number, Quantity_TOC_RGB), XCAFDoc_ColorSurf);
      }
    }
    STEPCAFControl_Writer wr;
    wr.SetNameMode(true);
    wr.SetColorMode(true);
    DESTEP_Parameters prm;
    prm.InitFromStatic();
    prm.WriteUnit = stepUnit;
    if (const Json* sc = req.Get("schema"); sc && sc->kind == Json::String && sc->text == "ap242")
      prm.WriteSchema = DESTEP_Parameters::WriteMode_StepSchema_AP242DIS;
    std::ostringstream os;
    if (!wr.Transfer(doc, prm) || wr.WriteStream(os) != IFSelect_RetDone)
      throw Refuse{"export.failed", "", "the STEP writer did not complete"};
    data = os.str();
  }
  else if (f == "stl" || f == "ply")
  {
    double lin, ang;
    Deflections(req, "linear_um", lin, ang);
    std::vector<double> xyz;
    std::vector<uint32_t> tris;
    for (auto& s : shapes)
    {
      Mesh m = Tessellate(s, lin, ang);
      uint32_t base = static_cast<uint32_t>(xyz.size() / 3);
      for (double v : m.xyz) xyz.push_back(v / per);
      for (uint32_t t : m.tris) tris.push_back(base + t);
    }
    if (f == "stl")
    {
      std::string hdr = "circuitRF geometry kernel, binary STL, units: " + units;
      hdr.resize(80, ' ');
      data = hdr;
      uint32_t n = static_cast<uint32_t>(tris.size() / 3);
      data += Bytes(&n, 4);
      for (size_t t = 0; t < tris.size(); t += 3)
      {
        float rec[12] = {0, 0, 0};
        for (int k = 0; k < 3; ++k)
          for (int j = 0; j < 3; ++j) rec[3 + 3 * k + j] = float(xyz[3 * tris[t + k] + j]);
        data += Bytes(rec, 48);
        data += std::string(2, '\0');
      }
    }
    else
    {
      // PLY merges coincident nodes, so a closed solid reads as closed (CSXCAD's polyhedron reader).
      std::map<std::array<double, 3>, uint32_t> index;
      std::vector<uint32_t> remap(xyz.size() / 3);
      std::vector<std::array<double, 3>> unique;
      for (size_t i = 0; i < remap.size(); ++i)
      {
        std::array<double, 3> k{xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]};
        auto [it, added] = index.emplace(k, static_cast<uint32_t>(unique.size()));
        if (added) unique.push_back(k);
        remap[i] = it->second;
      }
      std::ostringstream o;
      o << "ply\nformat ascii 1.0\ncomment circuitRF geometry kernel, units: " << units << "\nelement vertex " << unique.size()
        << "\nproperty double x\nproperty double y\nproperty double z\nelement face " << tris.size() / 3
        << "\nproperty list uchar int vertex_indices\nend_header\n";
      char b[96];
      for (auto& p : unique)
      {
        std::snprintf(b, sizeof b, "%.17g %.17g %.17g\n", p[0], p[1], p[2]);
        o << b;
      }
      for (size_t t = 0; t < tris.size(); t += 3)
        o << "3 " << remap[tris[t]] << " " << remap[tris[t + 1]] << " " << remap[tris[t + 2]] << "\n";
      data = o.str();
    }
  }
  else throw Refuse{"request.malformed", "", "\"format\" is not one of brep, step, ply, stl"};

  Reply r;
  r.j.Str("format", f).Str("units", units);
  r.Blob("data", "bytes", data.size(), data);
  return r.Finish();
}

struct ReadPart
{
  std::string name, path;
  bool hasColor = false;
  double rgb[3] = {0, 0, 0};
  TopoDS_Shape shape;
};

static void Collect(const occ::handle<XCAFDoc_ShapeTool>& st, const occ::handle<XCAFDoc_ColorTool>& ct, const TDF_Label& l,
                    const std::string& path, std::vector<ReadPart>& out)
{
  TDF_Label ref = l;
  if (st->IsReference(l)) st->GetReferredShape(l, ref);
  if (st->IsAssembly(ref))
  {
    NCollection_Sequence<TDF_Label> comps;
    st->GetComponents(ref, comps);
    for (int i = 1; i <= comps.Length(); ++i) Collect(st, ct, comps(i), path + "/" + std::to_string(i), out);
    return;
  }
  ReadPart p;
  p.path = path;
  occ::handle<TDataStd_Name> nm;
  if (l.FindAttribute(TDataStd_Name::GetID(), nm) || ref.FindAttribute(TDataStd_Name::GetID(), nm))
    p.name = TCollection_AsciiString(nm->Get()).ToCString();
  Quantity_Color c;
  for (TDF_Label q : {l, ref})
    for (XCAFDoc_ColorType ty : {XCAFDoc_ColorSurf, XCAFDoc_ColorGen, XCAFDoc_ColorCurv})
      if (!p.hasColor && ct->GetColor(q, ty, c)) { p.hasColor = true; c.Values(p.rgb[0], p.rgb[1], p.rgb[2], Quantity_TOC_RGB); }
  p.shape = st->GetShape(l);  // located: the assembly's placement is applied
  if (!p.hasColor && ct->GetColor(p.shape, XCAFDoc_ColorSurf, c)) { p.hasColor = true; c.Values(p.rgb[0], p.rgb[1], p.rgb[2], Quantity_TOC_RGB); }
  out.push_back(p);
}

static Frame OpImportStep(const Json& req, const std::map<std::string, std::string>& blobs)
{
  const Json* prefix = req.Get("shape");
  if (prefix == nullptr || prefix->kind != Json::String || prefix->text.empty())
    throw Refuse{"request.malformed", "", "import-step needs \"shape\": the handle prefix its parts are held under"};

  STEPCAFControl_Reader rd;
  rd.SetNameMode(true);
  rd.SetColorMode(true);
  IFSelect_ReturnStatus status;
  auto file = blobs.find("file");
  if (file != blobs.end())
  {
    std::istringstream is(file->second);
    status = rd.ReadStream("import.step", is);
  }
  else if (const Json* p = req.Get("path"); p && p->kind == Json::String)
    status = rd.ReadFile(p->text.c_str());
  else
    throw Refuse{"request.malformed", "", "import-step needs the file as a \"file\" blob or a \"path\""};
  if (status != IFSelect_RetDone) throw Refuse{"import.failed", "", "the file is not a STEP file the reader can read"};

  NCollection_Sequence<TCollection_AsciiString> len, ang, sol;
  rd.ChangeReader().FileUnits(len, ang, sol);
  occ::handle<TDocStd_Document> doc = NewXcafDocument();
  if (!rd.Transfer(doc)) throw Refuse{"import.failed", "", "the STEP reader could not transfer the file's shapes"};
  occ::handle<XCAFDoc_ShapeTool> st = XCAFDoc_DocumentTool::ShapeTool(doc->Main());
  occ::handle<XCAFDoc_ColorTool> ct = XCAFDoc_DocumentTool::ColorTool(doc->Main());
  NCollection_Sequence<TDF_Label> free;
  st->GetFreeShapes(free);
  std::vector<ReadPart> parts;
  for (int i = 1; i <= free.Length(); ++i) Collect(st, ct, free(i), std::to_string(i), parts);

  Reply r;
  std::vector<std::string> healing;
  r.j.Key("units").BeginArr();
  for (int i = 1; i <= len.Length(); ++i) r.j.Str(len(i).ToCString());
  r.j.EndArr();
  r.j.Key("parts").BeginArr();
  for (size_t i = 0; i < parts.size(); ++i)
  {
    ReadPart& p = parts[i];
    // Only a part that fails the validity check is healed: ShapeFix reports "done" for the tolerance
    // touch-ups every translated file needs, which would make every report say every part was repaired.
    std::string who = "part " + p.path + (p.name.empty() ? "" : " '" + p.name + "'");
    if (!Valid(p.shape))
    {
      ShapeFix_Shape fix(p.shape);
      fix.Perform();
      p.shape = fix.Shape();
      healing.push_back(who + (Valid(p.shape) ? ": invalid as read; shape healing repaired it"
                                              : ": invalid as read, and still invalid after shape healing"));
    }
    Held h{p.shape, {}};
    Shapes faces;
    TopExp::MapShapes(p.shape, TopAbs_FACE, faces);
    for (int k = 1; k <= faces.Extent(); ++k) h.faceNames.push_back("face" + std::to_string(k));
    std::string handle = prefix->text + "/" + std::to_string(i + 1);
    g_shapes[handle] = h;
    Counts c = Count(p.shape);
    r.j.Begin().Str("shape", handle).Str("name", p.name).Str("path", p.path);
    r.j.Key("colour");
    if (p.hasColor) r.j.BeginArr().Num(p.rgb[0]).Num(p.rgb[1]).Num(p.rgb[2]).EndArr();
    else r.j.Null();
    r.j.Int("solids", c.solids).Int("faces", c.faces).Bool("valid", Valid(p.shape)).End();
  }
  r.j.EndArr();
  r.j.Key("healing").BeginArr();
  for (auto& s : healing) r.j.Str(s);
  r.j.EndArr();
  return r.Finish();
}

static Frame OpRelease(const Json& req)
{
  const Json* list = req.Get("shapes");
  if (list == nullptr || list->kind != Json::Array) throw Refuse{"request.malformed", "", "release needs \"shapes\""};
  int released = 0;
  for (const Json& h : list->items)
    if (h.kind == Json::String) released += static_cast<int>(g_shapes.erase(h.text));
  Reply r;
  r.j.Int("released", released).Int("held", static_cast<long long>(g_shapes.size()));
  return r.Finish();
}

static Frame OpBox(const Json& req)
{
  const Json* size = req.Get("size_um");
  if (size == nullptr || size->kind != Json::Array || size->items.size() != 3)
    throw Refuse{"request.malformed", "", "box needs \"size_um\": three lengths in micrometres, [x, y, z]"};
  double d[3];
  for (int i = 0; i < 3; ++i)
  {
    const Json& v = size->items[i];
    if (v.kind != Json::Number || !std::isfinite(v.number) || v.number <= 0)
      throw Refuse{"request.malformed", "", "box needs three positive, finite lengths in micrometres"};
    d[i] = v.number;
  }
  TopoDS_Shape box = BRepPrimAPI_MakeBox(d[0], d[1], d[2]).Shape();
  if (!Valid(box)) throw Refuse{"build.invalid-result", "", "the kernel built a box that does not pass its own validity check"};
  Counts c = Count(box);
  Reply r;
  r.j.Int("solids", c.solids).Int("faces", c.faces).Num("volume_um3", Volume(box));
  return r.Finish();
}

// A 1000 x 800 x 500 um block with a 150 um bore through it, and the bore's top rim filleted at 25 um.
// The same part brief 61's `selftest` measured (testdata/em3d/f4b/harness), so a number here can be
// read against that run.
static TopoDS_Shape BoredBlock(double filletR)
{
  TopoDS_Shape block = BRepPrimAPI_MakeBox(gp_Pnt(0, 0, 0), gp_Pnt(1000, 800, 500)).Shape();
  TopoDS_Shape bore = BRepPrimAPI_MakeCylinder(gp_Ax2(gp_Pnt(500, 400, -100), gp::DZ()), 150, 700).Shape();
  BRepAlgoAPI_Cut cut(block, bore);
  if (!cut.IsDone()) return TopoDS_Shape();
  TopoDS_Shape s = cut.Shape();
  if (filletR <= 0) return s;

  BRepFilletAPI_MakeFillet mf(s);
  for (TopExp_Explorer x(s, TopAbs_EDGE); x.More(); x.Next())
  {
    Bnd_Box b;
    BRepBndLib::AddOptimal(x.Current(), b, false, false);
    double x0, y0, z0, x1, y1, z1;
    b.Get(x0, y0, z0, x1, y1, z1);
    // the rim: an edge lying in the top face, strictly inside the block's outline
    if (std::abs(z0 - 500) < 1e-6 && std::abs(z1 - 500) < 1e-6 && x0 > 1 && x1 < 999)
      mf.Add(filletR, TopoDS::Edge(x.Current()));
  }
  mf.Build();
  return mf.IsDone() ? mf.Shape() : TopoDS_Shape();
}

// Write the part to STEP in memory, read it back, and compare. `name` comes back as the one part's
// name; the volume is compared by the caller.
static bool StepRoundTrip(const TopoDS_Shape& part, std::string& name, double& volume)
{
  occ::handle<TDocStd_Document> doc = NewXcafDocument();
  occ::handle<XCAFDoc_ShapeTool> st = XCAFDoc_DocumentTool::ShapeTool(doc->Main());
  occ::handle<XCAFDoc_ColorTool> ct = XCAFDoc_DocumentTool::ColorTool(doc->Main());
  TDF_Label l = st->AddShape(part, false);
  TDataStd_Name::Set(l, TCollection_ExtendedString("lid"));
  ct->SetColor(l, Quantity_Color(0.83, 0.69, 0.22, Quantity_TOC_RGB), XCAFDoc_ColorSurf);

  STEPCAFControl_Writer wr;
  wr.SetNameMode(true);
  wr.SetColorMode(true);
  DESTEP_Parameters prm;
  prm.InitFromStatic();
  prm.WriteUnit = UnitsMethods_LengthUnit_Micron;
  std::ostringstream os;
  if (!wr.Transfer(doc, prm) || wr.WriteStream(os) != IFSelect_RetDone) return false;

  std::istringstream is(os.str());
  STEPCAFControl_Reader rd;
  rd.SetNameMode(true);
  rd.SetColorMode(true);
  if (rd.ReadStream("selftest.step", is) != IFSelect_RetDone) return false;
  occ::handle<TDocStd_Document> back = NewXcafDocument();
  if (!rd.Transfer(back)) return false;

  occ::handle<XCAFDoc_ShapeTool> bst = XCAFDoc_DocumentTool::ShapeTool(back->Main());
  NCollection_Sequence<TDF_Label> free;
  bst->GetFreeShapes(free);
  if (free.Length() != 1) return false;
  occ::handle<TDataStd_Name> nm;
  if (free(1).FindAttribute(TDataStd_Name::GetID(), nm)) name = TCollection_AsciiString(nm->Get()).ToCString();
  TopoDS_Shape shape = bst->GetShape(free(1));
  volume = Volume(shape);
  return Valid(shape);
}

static Frame OpSelftest()
{
  const double r = 25, R = 150;
  TopoDS_Shape cut = BoredBlock(0);
  TopoDS_Shape fil = BoredBlock(r);

  const double vCutRef = 1000.0 * 800 * 500 - kPi * R * R * 500;
  // Pappus: the fillet removes a spandrel of area r^2 (1 - pi/4), whose centroid sits r (10 - 3 pi) /
  // (3 (4 - pi)) outside the bore's radius, swept once around it.
  const double xbar = r * (10 - 3 * kPi) / (3 * (4 - kPi));
  const double vFilRef = vCutRef - r * r * (1 - kPi / 4) * 2 * kPi * (R + xbar);

  Counts cc = Count(cut), fc = Count(fil);
  double vCut = Volume(cut), vFil = Volume(fil);
  bool valid = Valid(cut) && Valid(fil);
  bool cutOk = cc.solids == 1 && cc.faces == 7 && std::abs(vCut / vCutRef - 1) < 1e-9;
  bool filOk = fc.solids == 1 && fc.faces == 8 && std::abs(vFil / vFilRef - 1) < 1e-6;

  std::string stepName;
  double stepVolume = 0;
  bool step = !fil.IsNull() && StepRoundTrip(fil, stepName, stepVolume) && stepName == "lid"
           && std::abs(stepVolume / vFil - 1) < 1e-9;

  if (!(valid && cutOk && filOk && step))
  {
    std::string what = !valid ? "a result failed the kernel's validity check"
                     : !cutOk ? "the block minus the cylinder came out with the wrong faces or volume"
                     : !filOk ? "the filleted rim came out with the wrong faces or volume"
                              : "the part did not survive a STEP write and read back in memory";
    throw Refuse{"selftest.failed", "", "the self-test failed: " + what};
  }
  Reply rep;
  rep.j.Int("faces", fc.faces).Bool("valid", valid).Bool("step_roundtrip", step).Num("volume_um3", vFil)
       .Num("volume_ref_um3", vFilRef).Str("occt", LoadedOcct());
  return rep.Finish();
}

static Frame Ok() { return Reply().Finish(); }

// One request. `quit` is set when the worker should exit after answering.
static Frame Dispatch(const Frame& in, bool& quit)
{
  Json req;
  try
  {
    req = JsonReader(in.json).Document();
  }
  catch (const std::exception& e)
  {
    return Refusal({"request.malformed", "", std::string("the request is not valid JSON: ") + e.what()});
  }
  const Json* op = req.Get("op");
  if (req.kind != Json::Object || op == nullptr || op->kind != Json::String)
    return Refusal({"request.malformed", "", "a request is a JSON object with an \"op\" member naming the operation"});

  const std::string& name = op->text;
  try
  {
    std::map<std::string, std::string> blobs = RequestBlobs(req, in.bin);
    if (name == "quit" || name == "shutdown") { quit = true; return Ok(); }
    if (!OcctMatches()) throw Refuse{"kernel.mismatch", "", MismatchSentence()};
    if (name == "hello") return OpHello(req);
    if (name == "build") return OpBuild(req);
    if (name == "tessellate") return OpTessellate(req);
    if (name == "faces") return OpFaces(req);
    if (name == "edges") return OpEdges(req);
    if (name == "export") return OpExport(req);
    if (name == "import-step") return OpImportStep(req, blobs);
    if (name == "release") return OpRelease(req);
    if (name == "box") return OpBox(req);
    if (name == "selftest") return OpSelftest();
    throw Refuse{"request.unknown", "", "this geometry worker does not know the operation \"" + name + "\""};
  }
  catch (const Refuse& r)
  {
    return Refusal(r);
  }
}

// ------------------------------------------------------------------------------------------------

static int PrintVersion()
{
  std::printf("geometry-worker %s\n", CRF_WORKER_VERSION);
  if (OcctMatches())
  {
    std::printf("occt %s\n", LoadedOcct().c_str());
    return 0;
  }
  std::printf("occt %s (built against %s -- MISMATCH)\n", LoadedOcct().c_str(), OCC_VERSION_COMPLETE);
  return 1;
}

int main(int argc, char** argv)
{
  if (argc == 2 && (std::strcmp(argv[1], "--version") == 0 || std::strcmp(argv[1], "-v") == 0)) return PrintVersion();
  if (argc != 1)
  {
    std::fprintf(stderr,
                 "usage: geometry-worker            speak the protocol on stdin/stdout (framed; see README.md)\n"
                 "       geometry-worker --version  print the worker and OCCT versions\n");
    return 2;
  }

  const char* test = std::getenv("CRF_GEOMETRY_WORKER_TEST");
  g_testOps = test != nullptr && std::strcmp(test, "1") == 0;

  // The protocol gets its own descriptor; fd 1 then points at stderr for everyone else.
  std::fflush(stdout);
  int protoFd = crf_dup(crf_fileno(stdout));
  if (protoFd >= 0 && crf_dup2(crf_fileno(stderr), crf_fileno(stdout)) >= 0)
    if (FILE* f = crf_fdopen(protoFd, "wb")) g_proto = f;
#if defined(_WIN32)
  _setmode(crf_fileno(g_proto), _O_BINARY);  // frames are bytes: no "\r\n" translation either way
  _setmode(crf_fileno(stdin), _O_BINARY);
#endif

  // OCCT's default messenger prints to standard output; with fd 1 on stderr that is now harmless, and
  // removing it keeps the diagnostics stream quiet too.
  Message::DefaultMessenger()->RemovePrinters(STANDARD_TYPE(Message_PrinterOStream));

  // A fault inside the kernel becomes an OSD_Signal exception rather than a dead process (Q9).
  OSD::SetSignal(false);

  Frame in;
  while (ReadFrame(in))
  {
    bool quit = false, fatal = false;
    Frame response;
    try
    {
      OCC_CATCH_SIGNALS
      response = Dispatch(in, quit);
    }
    catch (const OSD_Signal& e)
    {
      std::string msg = e.what() ? e.what() : "";
      response = Refusal({"kernel.fault", "", std::string(e.ExceptionType()) + (msg.empty() ? "" : ": " + msg)});
      fatal = true;
    }
    catch (const Standard_Failure& e)
    {
      std::string msg = e.what() ? e.what() : "";
      response = Refusal({"kernel.exception", "", std::string(e.ExceptionType()) + (msg.empty() ? "" : ": " + msg)});
    }
    catch (const std::exception& e)
    {
      response = Refusal({"kernel.exception", "", e.what()});
    }
    catch (...)
    {
      response = Refusal({"kernel.exception", "", "an unknown exception"});
    }

    WriteFrame(response);
    if (fatal)
    {
      std::fprintf(stderr, "geometry-worker: exiting after a caught signal; the heap cannot be trusted\n");
      return 3;
    }
    if (quit) return 0;
  }
  return 0;
}
