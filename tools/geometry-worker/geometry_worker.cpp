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
//      loops        a held shape -> per face: a plane or not, and a plane's boundary loops in micrometres (brief 130)
//      export       held shapes -> B-rep, STEP, PLY or STL bytes
//      write-step   the elaborated model -> one STEP file: parts cut by precedence, assemblies, header (brief 69)
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

#include <APIHeaderSection_MakeHeader.hxx>
#include <BRepAdaptor_Curve.hxx>
#include <BRepAdaptor_Surface.hxx>
#include <BRepAlgoAPI_Common.hxx>
#include <BRepAlgoAPI_Cut.hxx>
#include <BRepAlgoAPI_Fuse.hxx>
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
#include <BRepExtrema_DistShapeShape.hxx>
#include <BRepFilletAPI_MakeChamfer.hxx>
#include <BRepFilletAPI_MakeFillet.hxx>
#include <BRepGProp.hxx>
#include <BRepLProp_CLProps.hxx>
#include <BRepLProp_SLProps.hxx>
#include <BRepLib.hxx>
#include <BRepMesh_IncrementalMesh.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <BRepPrimAPI_MakePrism.hxx>
#include <BRepPrimAPI_MakeSphere.hxx>
#include <BRepFill_Generator.hxx>
#include <BRepTools.hxx>
#include <BRepTools_WireExplorer.hxx>
#include <BRep_Builder.hxx>
#include <BRep_Tool.hxx>
#include <Bnd_Box.hxx>
#include <DESTEP_Parameters.hxx>
#include <GCPnts_AbscissaPoint.hxx>
#include <GCPnts_QuasiUniformDeflection.hxx>
#include <GProp_GProps.hxx>
#include <GeomAPI_Interpolate.hxx>
#include <GeomAPI_ProjectPointOnSurf.hxx>
#include <Message.hxx>
#include <Message_Messenger.hxx>
#include <Message_PrinterOStream.hxx>
#include <NCollection_IndexedDataMap.hxx>
#include <NCollection_IndexedMap.hxx>
#include <OSD.hxx>
#include <OSD_Signal.hxx>
#include <Poly_Triangulation.hxx>
#include <Quantity_Color.hxx>
#include <Quantity_ColorRGBA.hxx>
#include <TColgp_HArray1OfPnt.hxx>
#include <STEPCAFControl_Reader.hxx>
#include <STEPCAFControl_Writer.hxx>
#include <STEPConstruct_UnitContext.hxx>
#include <ShapeAnalysis_ShapeTolerance.hxx>
#include <StepBasic_ConversionBasedUnit.hxx>
#include <StepBasic_ConversionBasedUnitAndLengthUnit.hxx>
#include <StepBasic_LengthUnit.hxx>
#include <StepBasic_SiUnit.hxx>
#include <StepBasic_SiUnitAndLengthUnit.hxx>
#include <StepData_Factors.hxx>
#include <StepData_StepModel.hxx>
#include <StepGeom_GeomRepContextAndGlobUnitAssCtxAndGlobUncertaintyAssCtx.hxx>
#include <StepGeom_GeometricRepresentationContextAndGlobalUnitAssignedContext.hxx>
#include <StepRepr_GlobalUnitAssignedContext.hxx>
#include <StepRepr_NextAssemblyUsageOccurrence.hxx>
#include <TCollection_HAsciiString.hxx>
#include <TopLoc_Location.hxx>
#include <XCAFDoc_DimTolTool.hxx>
#include <XSControl_Reader.hxx>
#include <ShapeBuild_ReShape.hxx>
#include <ShapeFix_Shape.hxx>
#include <ShapeUpgrade_UnifySameDomain.hxx>
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
#include <functional>
#include <fstream>
#include <iostream>
#include <map>
#include <type_traits>
#include <memory>
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

// What this worker ANSWERS, as distinct from the protocol it speaks: raised whenever the same tree gets a different
// reply (3D editor bugs round 5 -- a boolean's coplanar pieces merged). The handshake reports it as "results" and the
// client files every cached reply under it, so a reply an older worker cached is never read back by this one --
// development builds share one VERSION, so the version alone would have served the old answer from the disk cache.
static const int kResults = 2;
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
// Every STEP file read since the worker started (import-step and each build's per-file cache miss), reported by
// hello so a test can count reads rather than time them (brief 127 R-em3d127-1d).
static long long g_stepReads = 0;

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
  // brief-em3d-67 R-em3d67-5d: which edges a fillet or chamfer failed on, so the client can name them in the user's
  // terms. `edges` is one edge that does not fit (with `width_um`, the narrower of the two faces beside it) or the
  // edges meeting at a corner the kernel cannot blend; `missing` is an edge name's faces the target no longer has.
  std::vector<std::string> edges;
  double width_um = -1;
  bool corner = false;
  std::vector<std::string> missing;
};

static Frame Refusal(const Refuse& r)
{
  JsonOut j;
  j.Begin().Bool("ok", false).Str("code", r.code).Str("object", r.object).Str("detail", r.detail);
  if (!r.edges.empty())
  {
    j.Key("edges").BeginArr();
    for (const std::string& e : r.edges) j.Str(e);
    j.EndArr().Bool("corner", r.corner);
  }
  if (r.width_um > 0) j.Num("width_um", r.width_um);
  if (!r.missing.empty())
  {
    j.Key("missing").BeginArr();
    for (const std::string& m : r.missing) j.Str(m);
    j.EndArr();
  }
  j.End();
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

// The order split pieces and repeated edges are numbered in (brief 64 section 2d): by centroid in the
// OBJECT'S OWN frame -- before its placement, so moving or rotating it never renumbers -- compared x,
// then y, then z, each rounded to 1 nm, so the order never depends on the last bit of a double. Brief
// 61 Q7: never OCCT's list order, which it measured to differ between runs of one build.
using GeoKey = std::array<long long, 3>;

static GeoKey CentroidKey(const TopoDS_Shape& s, const gp_Trsf& toOwn)
{
  GProp_GProps p;
  if (s.ShapeType() == TopAbs_EDGE) BRepGProp::LinearProperties(s, p);
  else BRepGProp::SurfaceProperties(s, p);
  gp_Pnt c = p.CentreOfMass();
  if (!(p.Mass() > 0))
  {
    Box6 b = Tight(s);
    c = gp_Pnt((b.x0 + b.x1) / 2, (b.y0 + b.y1) / 2, (b.z0 + b.z1) / 2);
  }
  c.Transform(toOwn);
  return {std::llround(c.X() * 1000), std::llround(c.Y() * 1000), std::llround(c.Z() * 1000)};  // um -> nm
}

template <class T>
static void SortByCentroid(std::vector<T>& list, const gp_Trsf& toOwn, const TopoDS_Shape& (*shapeOf)(const T&))
{
  std::vector<std::pair<GeoKey, size_t>> keys;
  for (size_t i = 0; i < list.size(); ++i) keys.push_back({CentroidKey(shapeOf(list[i]), toOwn), i});
  std::stable_sort(keys.begin(), keys.end(), [](const auto& a, const auto& b) { return a.first < b.first; });
  std::vector<T> sorted;
  for (auto& [k, i] : keys) sorted.push_back(list[i]);
  list = sorted;
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
  gp_Trsf toOwn;                       // world -> the root object's own frame (brief 64 section 2d's numbering frame)
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
  gp_Trsf toOwn;  // identity while a node is built in its own frame; its transform's inverse once carried by it
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

// brief-em3d-102 R-em3d102-3b -- one face, named names[0]. Its seam and its two degenerate pole edges are not feature
// edges (NameEdges leaves a seam and a degenerate edge out), so a sphere alone has no edge, and a sphere cut from a box
// adds only the circles where the two surfaces meet.
static Named BuildSphere(const NodeReader& r, const std::vector<std::string>& names)
{
  if (names.size() != 1) r.Bad("a sphere names one face: surface");
  gp_Pnt centre(r.Xyz("centre"));
  double radius = r.Num("radius");
  if (radius <= 0) r.Bad("the radius is not positive");
  TopoDS_Shape s = BRepPrimAPI_MakeSphere(centre, radius).Shape();
  Named n{s, {}};
  for (TopExp_Explorer x(s, TopAbs_FACE); x.More(); x.Next()) n.faces.push_back({x.Current(), names[0]});
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


// ------------------------------------------------------------------------------------------------
// edges by name (overview section 1g, as brief 61 Q7 corrected it; brief 64 section 2b)
// ------------------------------------------------------------------------------------------------

// A feature edge: the edge, its name, and the two faces it separates (their names sorted, and shapes).
struct NamedEdge
{
  TopoDS_Edge edge;
  std::string name;
  std::array<std::string, 2> faceNames;
  std::array<TopoDS_Face, 2> faces;
};

static const TopoDS_Shape& EdgeOf(const NamedEdge& e) { return e.edge; }

// Edges are named by the two faces they separate, sorted and joined by '|'; where a pair bounds more
// than one edge a third field numbers them in section 2d's order. A seam -- a face meeting itself --
// and a degenerate edge are not feature edges and are left out.
static std::vector<NamedEdge> NameEdges(const TopoDS_Shape& shape, const std::vector<std::string>& faceNames, const gp_Trsf& toOwn)
{
  Shapes faces;
  TopExp::MapShapes(shape, TopAbs_FACE, faces);
  Ancestors anc;
  TopExp::MapShapesAndAncestors(shape, TopAbs_EDGE, TopAbs_FACE, anc);
  std::map<std::string, std::vector<NamedEdge>> byPair;
  for (int i = 1; i <= anc.Extent(); ++i)
  {
    TopoDS_Edge e = TopoDS::Edge(anc.FindKey(i));
    if (BRep_Tool::Degenerated(e)) continue;
    Shapes distinct;
    for (const TopoDS_Shape& f : anc(i)) distinct.Add(f);
    if (distinct.Extent() != 2) continue;  // a seam (one face) or a non-manifold edge
    NamedEdge ne;
    ne.edge = e;
    ne.faces = {TopoDS::Face(distinct(1)), TopoDS::Face(distinct(2))};
    ne.faceNames = {faceNames[faces.FindIndex(distinct(1)) - 1], faceNames[faces.FindIndex(distinct(2)) - 1]};
    if (ne.faceNames[1] < ne.faceNames[0])
    {
      std::swap(ne.faceNames[0], ne.faceNames[1]);
      std::swap(ne.faces[0], ne.faces[1]);
    }
    byPair[ne.faceNames[0] + "|" + ne.faceNames[1]].push_back(ne);
  }
  std::vector<NamedEdge> out;
  for (auto& [key, list] : byPair)
  {
    SortByCentroid(list, toOwn, &EdgeOf);
    for (size_t k = 0; k < list.size(); ++k)
    {
      list[k].name = list.size() > 1 ? key + "|" + std::to_string(k + 1) : key;
      out.push_back(list[k]);
    }
  }
  return out;
}

// ------------------------------------------------------------------------------------------------
// operation nodes (brief 64): boolean, fillet, chamfer, step -- named from OCCT's own history
// ------------------------------------------------------------------------------------------------

static Named BuildNode(const Json& node);
static Held Hold(const Named& n);

// The name before any piece number: "zmax#2" -> "zmax".
static std::string BaseName(const std::string& n)
{
  size_t h = n.find('#');
  return h == std::string::npos ? n : n.substr(0, h);
}

using FacePiece = std::pair<TopoDS_Shape, std::string>;
static const TopoDS_Shape& PieceOf(const FacePiece& p) { return p.first; }

// Gives every face of an operation's RESULT its name. `claims` are (source face, name) in priority
// order -- the blank's before a tool's -- and `history` maps a source face to what became of it. A
// face of the result keeps the first name that claims it. Then every base name held by more than one
// face is numbered #1..#n in section 2d's order: the pieces of a split face, including a face an
// operand had already split, which is renumbered with its siblings rather than suffixed twice.
template <class Op>
static Named NameResult(const TopoDS_Shape& result, const std::vector<FacePiece>& claims, Op& history)
{
  Shapes faces;
  TopExp::MapShapes(result, TopAbs_FACE, faces);
  std::vector<std::string> nameOf(faces.Extent());
  auto claim = [&](const TopoDS_Shape& f, const std::string& nm) {
    int i = faces.FindIndex(f);
    if (i > 0 && nameOf[i - 1].empty()) nameOf[i - 1] = BaseName(nm);
  };
  for (const auto& [f, nm] : claims)
  {
    if (history.IsDeleted(f)) continue;
    const NCollection_List<TopoDS_Shape>& mod = history.Modified(f);
    if (mod.IsEmpty()) claim(f, nm);
    else
      for (const TopoDS_Shape& g : mod) claim(g, nm);
  }
  Named n{result, {}, gp_Trsf()};
  std::map<std::string, std::vector<FacePiece>> byBase;
  for (int i = 1; i <= faces.Extent(); ++i)
    if (!nameOf[i - 1].empty()) byBase[nameOf[i - 1]].push_back({faces(i), nameOf[i - 1]});
  for (auto& [base, list] : byBase)
  {
    if (list.size() == 1) { n.faces.push_back(list[0]); continue; }
    SortByCentroid(list, gp_Trsf(), &PieceOf);
    for (size_t k = 0; k < list.size(); ++k) n.faces.push_back({list[k].first, base + "#" + std::to_string(k + 1)});
  }
  return n;
}

// 3D editor bugs round 5 -- a boolean's history, then the same-domain merge's after it: what NameResult reads for a
// result whose coplanar pieces were merged. A source face's images are the boolean's images (or itself, unchanged),
// each followed through the merge; a face the merge folded into its neighbour is claimed as the merged face, so the
// merged face keeps the FIRST name that claims it -- the blank's, which claims before any tool's.
template <class Op>
struct ThenMerged
{
  Op& first;
  Handle(BRepTools_History) merge;
  NCollection_List<TopoDS_Shape> images;

  bool IsDeleted(const TopoDS_Shape& f) { return first.IsDeleted(f); }

  const NCollection_List<TopoDS_Shape>& Modified(const TopoDS_Shape& f)
  {
    images.Clear();
    auto follow = [&](const TopoDS_Shape& g) {
      if (merge.IsNull()) { images.Append(g); return; }
      if (merge->IsRemoved(g)) return;
      const NCollection_List<TopoDS_Shape>& m = merge->Modified(g);
      if (m.IsEmpty()) images.Append(g);
      else
        for (const TopoDS_Shape& h : m) images.Append(h);
    };
    const NCollection_List<TopoDS_Shape>& mod = first.Modified(f);
    if (mod.IsEmpty()) follow(f);
    else
      for (const TopoDS_Shape& g : mod) follow(g);
    return images;
  }
};

static bool HasSolid(const TopoDS_Shape& s)
{
  return !s.IsNull() && TopExp_Explorer(s, TopAbs_SOLID).More();
}

static std::string OperandName(const Json& node)
{
  const Json* nm = node.Get("name");
  return nm && nm->kind == Json::String ? nm->text : "";
}

static Named BuildBoolean(const NodeReader& r)
{
  const Json& opv = r.Member("op");
  if (opv.kind != Json::String || (opv.text != "subtract" && opv.text != "unite" && opv.text != "intersect"))
    r.Bad("\"op\" is not one of subtract, unite, intersect");
  Named blank = BuildNode(r.Member("blank"));
  const Json& tools = r.Member("tools");
  if (tools.kind != Json::Array || tools.items.empty()) r.Bad("\"tools\" is not a list of one or more nodes");

  std::vector<FacePiece> claims(blank.faces.begin(), blank.faces.end());
  NCollection_List<TopoDS_Shape> args, toolShapes;
  args.Append(blank.shape);
  for (const Json& t : tools.items)
  {
    Named tool = BuildNode(t);
    std::string prefix = OperandName(t);
    if (prefix.empty()) r.Bad("a tool has no name, and a tool's faces are named after it");
    toolShapes.Append(tool.shape);
    for (auto& [f, nm] : tool.faces) claims.push_back({f, prefix + ":" + nm});
  }

  auto run = [&](auto& op) -> Named {
    op.SetArguments(args);
    op.SetTools(toolShapes);
    op.SetRunParallel(false);
    op.Build();
    if (op.HasErrors() || !op.IsDone())
    {
      std::ostringstream why;
      op.DumpErrors(why);
      std::string text = why.str();
      while (!text.empty() && (text.back() == '\n' || text.back() == ' ')) text.pop_back();
      throw Refuse{"build.failed", r.name, "the " + opv.text + " failed" + (text.empty() ? "" : ": " + text)};
    }
    if (!HasSolid(op.Shape()))
      // brief-em3d-66: its own code, so the client can say it in the operation's words ("'lid' and 'pin' share nothing").
      throw Refuse{"build.empty", r.name, "the " + opv.text + " leaves nothing: the result has no volume"};
    // 3D editor bugs round 5 -- ONE solid, not its operands' faces cut where they met: OCCT's boolean keeps a
    // face the operands shared a plane on as separate pieces (two boxes of one height united have a top in three),
    // and every seam between them was a drawn edge, a pickable face and an edge a fillet could name. The merge
    // joins same-domain neighbours (a plane with a coplanar plane, a cylinder with its coaxial continuation) and
    // nothing else, so a real crease is never lost; a merge that fails leaves the unmerged result, as it was.
    TopoDS_Shape result = op.Shape();
    Handle(BRepTools_History) merged;
    try
    {
      OCC_CATCH_SIGNALS
      ShapeUpgrade_UnifySameDomain unify(result, Standard_True, Standard_True, Standard_False);
      unify.Build();
      if (HasSolid(unify.Shape())) { result = unify.Shape(); merged = unify.History(); }
    }
    catch (const Standard_Failure&) { /* the unmerged result */ }
    ThenMerged<std::remove_reference_t<decltype(op)>> history{op, merged, {}};
    return NameResult(result, claims, history);
  };
  if (opv.text == "subtract") { BRepAlgoAPI_Cut op; return run(op); }
  if (opv.text == "unite") { BRepAlgoAPI_Fuse op; return run(op); }
  BRepAlgoAPI_Common op;
  return run(op);
}

// The name a face piece was split from, every piece number off: "zmax#2" -> "zmax", a managed fold's "zmax.1" ->
// "zmax" (brief 40 section 1e), and both at once. Only a numeric suffix is a piece number: "hole0.side0" is itself.
static std::string FoldBase(std::string n)
{
  for (;;)
  {
    size_t at = n.find_last_of("#.");
    if (at == std::string::npos || at + 1 >= n.size()) return n;
    for (size_t i = at + 1; i < n.size(); ++i)
      if (n[i] < '0' || n[i] > '9') return n;
    n.resize(at);
  }
}

static std::vector<std::string> SplitBar(const std::string& s)
{
  std::vector<std::string> out;
  size_t from = 0;
  for (;;)
  {
    size_t at = s.find('|', from);
    out.push_back(s.substr(from, at == std::string::npos ? std::string::npos : at - from));
    if (at == std::string::npos) return out;
    from = at + 1;
  }
}

// The target's edges named as a Fillet or Chamfer lists them, and each listed name's edges.
struct EdgeLookup
{
  std::vector<NamedEdge> all;

  // brief-em3d-67 R-em3d67-2d -- a name resolves to the edge of that name; failing that, a two-face name resolves to
  // EVERY edge between the pieces of its two faces (the fold rule applied on both sides), so a fillet on xmax|zmax
  // follows zmax folded into zmax.0 and zmax.1 onto both new edges. A numbered name (side|zmax|2) or a name of a piece
  // (xmax|zmax#1) resolves exactly or not at all: widening it would be a guess (em-3d.md section 6.4).
  std::vector<const NamedEdge*> Find(const std::string& name) const
  {
    std::vector<const NamedEdge*> out;
    for (const auto& e : all)
      if (e.name == name) out.push_back(&e);
    if (!out.empty()) return out;
    std::vector<std::string> f = SplitBar(name);
    if (f.size() != 2 || FoldBase(f[0]) != f[0] || FoldBase(f[1]) != f[1]) return out;
    for (const auto& e : all)
    {
      std::string a = FoldBase(e.faceNames[0]), b = FoldBase(e.faceNames[1]);
      if ((a == f[0] && b == f[1]) || (a == f[1] && b == f[0])) out.push_back(&e);
    }
    return out;
  }

  const NamedEdge* Of(const TopoDS_Shape& edge) const
  {
    for (const auto& e : all)
      if (e.edge.IsSame(edge)) return &e;
    return nullptr;
  }
};

// The faces an edge name names that the target has no face (nor piece of a face) called.
static std::vector<std::string> MissingFaces(const std::string& name, const std::vector<std::string>& faceNames)
{
  std::vector<std::string> out;
  std::vector<std::string> f = SplitBar(name);
  for (size_t k = 0; k < std::min<size_t>(f.size(), 2); ++k)
  {
    if (f[k].empty()) continue;
    bool has = false;
    for (const std::string& n : faceNames) has = has || n == f[k] || FoldBase(n) == f[k];
    if (!has) out.push_back(f[k]);
  }
  return out;
}

static std::vector<std::string> EdgeNames(const NodeReader& r)
{
  const Json& v = r.Member("edges");
  if (v.kind != Json::Array || v.items.empty()) r.Bad("\"edges\" is not a list of one or more edge names");
  std::vector<std::string> out;
  for (const Json& e : v.items)
  {
    if (e.kind != Json::String) r.Bad("\"edges\" holds something that is not an edge name");
    out.push_back(e.text);
  }
  return out;
}

// Names every face the operation made after the edge it came from -- every edge of each CONTOUR, not only the listed
// ones: a tangent chain propagates, and brief 61 Q7 left 7 of 18 faces unnamed when it named from the listed edge alone.
// `mk` has been built.
template <class Maker>
static Named FinishLocal(Maker& mk, const Named& target, const EdgeLookup& edges, const char* word)
{
  std::vector<FacePiece> claims(target.faces.begin(), target.faces.end());
  Named n = NameResult(mk.Shape(), claims, mk);
  Shapes named;
  for (auto& [f, nm] : n.faces) named.Add(f);
  Shapes faces;
  TopExp::MapShapes(mk.Shape(), TopAbs_FACE, faces);
  for (int c = 1; c <= mk.NbContours(); ++c)
    for (int i = 1; i <= mk.NbEdges(c); ++i)
    {
      const TopoDS_Edge& e = mk.Edge(c, i);
      const NamedEdge* ne = edges.Of(e);
      if (ne == nullptr) continue;
      for (const TopoDS_Shape& g : mk.Generated(e))
        if (g.ShapeType() == TopAbs_FACE && faces.Contains(g) && !named.Contains(g))
        {
          named.Add(g);
          n.faces.push_back({g, std::string(word) + "(" + ne->name + ")"});
        }
    }
  return n;
}

// Whether a maker built a solid. An exception is the kernel saying no; a fault (OSD_Signal) is left to the request
// boundary, which answers it and exits (README "Failure").
template <class Maker>
static bool Built(Maker& mk)
{
  try
  {
    mk.Build();
    return mk.IsDone() && HasSolid(mk.Shape());
  }
  catch (const OSD_Signal&) { throw; }
  catch (const Standard_Failure&) { return false; }
}

// brief-em3d-67 R-em3d67-5d -- how far the narrower of the two faces beside an edge reaches from it: the farthest of
// each face's vertices from the edge, the lesser of the two. A radius or distance must be less than this.
static double WidthBeside(const NamedEdge& e)
{
  double w = -1;
  for (const TopoDS_Face& f : e.faces)
  {
    double reach = 0;
    for (TopExp_Explorer x(f, TopAbs_VERTEX); x.More(); x.Next())
    {
      BRepExtrema_DistShapeShape d(x.Current(), e.edge);
      if (d.IsDone() && d.NbSolution() > 0) reach = std::max(reach, d.Value());
    }
    if (reach > 0 && (w < 0 || reach < w)) w = reach;
  }
  return w;
}

// Rounds or cuts the named edges of the target. A name that resolves to nothing is refused naming it and the face it
// has lost (edge.missing); an operation OCCT cannot build is diagnosed before it is refused: each name alone, so the
// one that does not fit is named with the width beside it; and when every name fits alone, the corner where two of
// them meet, named by the edges meeting there (R-em3d67-5d). Only a failure costs the extra builds.
template <class Maker, class Add>
static Named LocalOperation(const NodeReader& r, const Named& target, const char* word, Add add)
{
  Held th = Hold(target);
  // Numbered in the target's own frame, as `edges` numbers it when the target is a root (section 2d).
  EdgeLookup edges{NameEdges(th.shape, th.faceNames, th.toOwn)};
  std::vector<std::string> names = EdgeNames(r);
  std::vector<std::vector<const NamedEdge*>> found;
  for (const std::string& nm : names)
  {
    auto f = edges.Find(nm);
    if (f.empty())
    {
      Refuse no{"edge.missing", r.name, "the target has no edge named '" + nm + "'"};
      no.edges = {nm};
      no.missing = MissingFaces(nm, th.faceNames);
      throw no;
    }
    found.push_back(f);
  }
  auto make = [&](const std::vector<size_t>& which) {
    auto mk = std::make_unique<Maker>(target.shape);
    for (size_t i : which)
      for (const NamedEdge* e : found[i]) add(*mk, *e);
    return mk;
  };
  std::vector<size_t> every(names.size());
  for (size_t i = 0; i < every.size(); ++i) every[i] = i;
  auto mk = make(every);
  if (Built(*mk)) return FinishLocal(*mk, target, edges, word);

  std::string failed = std::string("the ") + word + " could not be built on those edges";
  for (size_t i = 0; i < names.size(); ++i)
  {
    if (names.size() > 1)
    {
      auto one = make({i});
      if (Built(*one)) continue;
    }
    Refuse no{"build.failed", r.name, failed};
    no.edges = {names[i]};
    for (const NamedEdge* e : found[i])
    {
      double w = WidthBeside(*e);
      if (w > 0 && (no.width_um < 0 || w < no.width_um)) no.width_um = w;
    }
    throw no;
  }
  // Every name fits alone: the corner where edges of two names meet.
  Refuse no{"build.failed", r.name, failed};
  no.corner = true;
  Ancestors byVertex;
  TopExp::MapShapesAndAncestors(th.shape, TopAbs_VERTEX, TopAbs_EDGE, byVertex);
  for (size_t i = 0; i < found.size() && no.edges.empty(); ++i)
    for (size_t j = i + 1; j < found.size() && no.edges.empty(); ++j)
      for (const NamedEdge* a : found[i])
        for (const NamedEdge* b : found[j])
        {
          if (!no.edges.empty()) break;
          for (TopExp_Explorer va(a->edge, TopAbs_VERTEX); va.More() && no.edges.empty(); va.Next())
            for (TopExp_Explorer vb(b->edge, TopAbs_VERTEX); vb.More(); vb.Next())
            {
              if (!va.Current().IsSame(vb.Current())) continue;
              int k = byVertex.FindIndex(va.Current());
              std::vector<std::string> at;
              if (k > 0)
                for (const TopoDS_Shape& e : byVertex(k))
                  if (const NamedEdge* ne = edges.Of(e); ne && std::find(at.begin(), at.end(), ne->name) == at.end())
                    at.push_back(ne->name);
              std::sort(at.begin(), at.end());
              no.edges = at;
              break;
            }
        }
  if (no.edges.empty()) no.corner = false;
  throw no;
}

static Named BuildFillet(const NodeReader& r)
{
  Named target = BuildNode(r.Member("target"));
  double radius = r.Num("radius");
  if (!(radius > 0)) r.Bad("the radius is not positive");
  return LocalOperation<BRepFilletAPI_MakeFillet>(r, target, "fillet",
    [radius](BRepFilletAPI_MakeFillet& mk, const NamedEdge& e) { mk.Add(radius, e.edge); });
}

static Named BuildChamfer(const NodeReader& r)
{
  Named target = BuildNode(r.Member("target"));
  double d1 = r.Num("distance");
  if (!(d1 > 0)) r.Bad("the distance is not positive");
  double d2 = 0;
  if (r.node.Get("distance2") != nullptr)
  {
    d2 = r.Num("distance2");
    if (!(d2 > 0)) r.Bad("the second distance is not positive");
  }
  // Distance lies on the edge's first face (its name sorts first), Distance2 on its second.
  return LocalOperation<BRepFilletAPI_MakeChamfer>(r, target, "chamfer",
    [d1, d2](BRepFilletAPI_MakeChamfer& mk, const NamedEdge& e) {
      if (d2 > 0) mk.Add(d1, d2, e.edge, e.faces[0]);
      else mk.Add(d1, e.edge);
    });
}

static Named BuildStep(const NodeReader& r);
static std::vector<std::string> g_buildNotes;

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

  Named n;
  if (kind.text == "boolean") n = BuildBoolean(r);
  else if (kind.text == "fillet") n = BuildFillet(r);
  else if (kind.text == "chamfer") n = BuildChamfer(r);
  else if (kind.text == "step") n = BuildStep(r);
  else
  {
    std::vector<std::string> names = r.FaceNames();
    if (kind.text == "box") n = BuildBox(r, names);
    else if (kind.text == "cylinder") n = BuildCylinder(r, names);
    else if (kind.text == "sphere") n = BuildSphere(r, names);
    else if (kind.text == "prism") n = BuildPrism(r, names);
    else if (kind.text == "polyhedron") n = BuildPolyhedron(r, names);
    else r.Bad("this worker cannot build a \"" + kind.text + "\"");
  }

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
      Named moved{xf.Shape(), {}, tr.Inverted()};
      for (auto& [f, nm] : n.faces) moved.faces.push_back({xf.ModifiedShape(f), nm});
      n = moved;
    }
  }
  return n;
}

static Held Hold(const Named& n)
{
  Held h{n.shape, {}, n.toOwn};
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
  r.j.Str("worker", CRF_WORKER_VERSION).Str("occt", LoadedOcct()).Int("protocol", kProtocol).Int("results", kResults).Str("rid", CompiledRid());
  r.j.Key("modules").BeginArr();
  for (const char* m : kModules) r.j.Str(m);
  r.j.EndArr();
  r.j.Bool("test_ops", g_testOps).Int("step_reads", g_stepReads);
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
  g_buildNotes.clear();
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
  notes.insert(notes.end(), g_buildNotes.begin(), g_buildNotes.end());

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
    r.j.Num("area", Area(f)).Num("min_radius", FaceMinRadius(f));
    // brief 68 R-em3d68-5c -- the fingerprint Reload from Source matches a face by: its centroid, and the outward
    // normal at the point of the face nearest it (the face's orientation applied).
    GProp_GProps gp;
    BRepGProp::SurfaceProperties(f, gp);
    gp_Pnt c = gp.CentreOfMass();
    r.j.Key("centroid").BeginArr().Num(c.X()).Num(c.Y()).Num(c.Z()).EndArr();
    gp_Dir nrm(0, 0, 1);
    BRepAdaptor_Surface sa(f);
    GeomAPI_ProjectPointOnSurf proj(c, BRep_Tool::Surface(f));
    double u = (sa.FirstUParameter() + sa.LastUParameter()) / 2, v = (sa.FirstVParameter() + sa.LastVParameter()) / 2;
    if (proj.NbPoints() > 0) proj.LowerDistanceParameters(u, v);
    BRepLProp_SLProps sp(sa, u, v, 1, 1e-9);
    bool have = sp.IsNormalDefined();
    if (have) nrm = sp.Normal();
    if (have && f.Orientation() == TopAbs_REVERSED) nrm.Reverse();
    r.j.Key("normal").BeginArr().Num(have ? nrm.X() : 0).Num(have ? nrm.Y() : 0).Num(have ? nrm.Z() : 0).EndArr();
    r.j.End();
  }
  r.j.EndArr();
  return r.Finish();
}

// brief-em3d-130 -- each face's boundary loops, in the "faces" order: whether the face is a plane, and per wire (the outer
// first, BRepTools::OuterWire, then the holes) its vertices in micrometres in the wire's order on the face, and whether every edge of
// it is a straight line. What turns a flat-faced STEP piece into a polyhedron, exactly or not at all; a curved face's loops
// are left out (nothing reads them).
static Frame OpLoops(const Json& req)
{
  Held& h = Find(req);
  Shapes faces;
  TopExp::MapShapes(h.shape, TopAbs_FACE, faces);
  Reply r;
  r.j.Key("faces").BeginArr();
  for (int i = 1; i <= faces.Extent(); ++i)
  {
    TopoDS_Face f = TopoDS::Face(faces(i));
    bool plane = BRepAdaptor_Surface(f).GetType() == GeomAbs_Plane;
    r.j.Begin().Str("name", h.faceNames[i - 1]).Bool("plane", plane);
    r.j.Key("loops").BeginArr();
    if (plane)
    {
      TopoDS_Wire outer = BRepTools::OuterWire(f);
      std::vector<TopoDS_Wire> wires;
      if (!outer.IsNull()) wires.push_back(outer);
      for (TopExp_Explorer x(f, TopAbs_WIRE); x.More(); x.Next())
        if (outer.IsNull() || !x.Current().IsSame(outer)) wires.push_back(TopoDS::Wire(x.Current()));
      for (const TopoDS_Wire& w : wires)
      {
        bool straight = true;
        double tol = 0;
        r.j.Begin();
        r.j.Key("points").BeginArr();
        for (BRepTools_WireExplorer we(w, f); we.More(); we.Next())
        {
          if (BRep_Tool::Degenerated(we.Current())) continue;
          if (BRepAdaptor_Curve(we.Current()).GetType() != GeomAbs_Line) straight = false;
          tol = std::max({tol, BRep_Tool::Tolerance(we.Current()), BRep_Tool::Tolerance(we.CurrentVertex())});
          gp_Pnt p = BRep_Tool::Pnt(we.CurrentVertex());
          r.j.Num(p.X()).Num(p.Y()).Num(p.Z());
        }
        // The B-rep's own tolerance on this loop, um: how far its vertices may sit from its edges and its face -- a file's
        // sloppiness, which a polyhedron through the vertices inherits.
        r.j.EndArr().Bool("straight", straight).Num("tolerance", tol).End();
      }
    }
    r.j.EndArr().End();
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

static Frame OpEdges(const Json& req)
{
  Held& h = Find(req);
  const Json* d = req.Get("deflection_um");
  if (d == nullptr || d->kind != Json::Number || !(d->number > 0))
    throw Refuse{"request.malformed", "", "\"deflection_um\" is not a positive number of micrometres"};

  Reply r;
  std::vector<double> poly;
  r.j.Key("edges").BeginArr();
  for (const NamedEdge& ne : NameEdges(h.shape, h.faceNames, h.toOwn))
  {
    BRepAdaptor_Curve c(ne.edge);
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
    double t0 = c.FirstParameter(), t1 = c.LastParameter();
    double length = GCPnts_AbscissaPoint::Length(c);
    r.j.Begin().Str("name", ne.name);
    r.j.Key("faces").BeginArr().Str(ne.faceNames[0]).Str(ne.faceNames[1]).EndArr();
    r.j.Str("kind", CurveKind(c.GetType()))
       .Num("length", length)
       .Num("min_radius", EdgeMinRadius(c))
       .Int("points", static_cast<long long>((poly.size() - before) / 3));
    // brief-em3d-67 R-em3d67-2c / -3e / -4: what snapping and the tangent chain need, exact from the curve rather than
    // read off the polyline (whose points move with the deflection). The ends and their tangents run the polyline's way;
    // a closed edge (a circle) has one vertex and no midpoint; a circle or an arc also gives its centre and radius.
    bool closed = TopExp::FirstVertex(ne.edge).IsSame(TopExp::LastVertex(ne.edge));
    gp_Pnt p0 = c.Value(t0), p1 = c.Value(t1);
    gp_Pnt pm0;
    gp_Vec d0, d1;
    c.D1(t0, pm0, d0);
    c.D1(t1, pm0, d1);
    if (d0.Magnitude() > 0) d0.Normalize();
    if (d1.Magnitude() > 0) d1.Normalize();
    r.j.Bool("closed", closed);
    r.j.Key("ends").BeginArr().Num(p0.X()).Num(p0.Y()).Num(p0.Z()).Num(p1.X()).Num(p1.Y()).Num(p1.Z()).EndArr();
    r.j.Key("tangents").BeginArr().Num(d0.X()).Num(d0.Y()).Num(d0.Z()).Num(d1.X()).Num(d1.Y()).Num(d1.Z()).EndArr();
    if (!closed)
    {
      GCPnts_AbscissaPoint half(c, length / 2, t0);
      gp_Pnt m = c.Value(half.IsDone() ? half.Parameter() : (t0 + t1) / 2);
      r.j.Key("mid").BeginArr().Num(m.X()).Num(m.Y()).Num(m.Z()).EndArr();
    }
    if (c.GetType() == GeomAbs_Circle)
    {
      gp_Circ circ = c.Circle();
      r.j.Key("centre").BeginArr().Num(circ.Location().X()).Num(circ.Location().Y()).Num(circ.Location().Z()).EndArr();
      r.j.Num("radius", circ.Radius());
    }
    r.j.End();
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
    // "assembly": true writes one assembly whose components are the shapes, each at its "locations" entry (twelve
    // numbers, a 3 x 4 matrix in rows, micrometres; null is identity) -- and a handle listed twice is ONE part
    // instanced twice, which is what a STEP file with repeated parts looks like.
    bool assembly = false;
    if (const Json* a = req.Get("assembly"); a && a->kind == Json::Bool) assembly = a->boolean;
    const Json* locations = req.Get("locations");
    TDF_Label top;
    std::map<std::string, TDF_Label> prototypes;
    if (assembly)
    {
      top = st->NewShape();
      TDataStd_Name::Set(top, TCollection_ExtendedString("assembly"));
    }
    for (size_t i = 0; i < shapes.size(); ++i)
    {
      const std::string& handle = list->items[i].text;
      bool again = assembly && prototypes.count(handle) > 0;
      TDF_Label l = again ? prototypes[handle] : st->AddShape(shapes[i], false);
      if (!again)
      {
        prototypes[handle] = l;
        if (names && names->kind == Json::Array && i < names->items.size() && names->items[i].kind == Json::String)
          TDataStd_Name::Set(l, TCollection_ExtendedString(names->items[i].text.c_str(), true));
        if (colours && colours->kind == Json::Array && i < colours->items.size())
        {
          const Json& c = colours->items[i];
          if (c.kind == Json::Array && c.items.size() == 3)
            // sRGB, the values a STEP file's COLOUR_RGB carries: OCCT's Quantity_TOC_RGB is LINEAR and is re-encoded on write.
            ct->SetColor(l, Quantity_Color(c.items[0].number, c.items[1].number, c.items[2].number, Quantity_TOC_sRGB), XCAFDoc_ColorSurf);
        }
      }
      if (!assembly) continue;
      gp_Trsf tr;
      if (locations && locations->kind == Json::Array && i < locations->items.size() && locations->items[i].kind == Json::Array)
      {
        const Json& m = locations->items[i];
        if (m.items.size() != 12) throw Refuse{"request.malformed", "", "a \"locations\" entry is not twelve numbers"};
        tr.SetValues(m.items[0].number, m.items[1].number, m.items[2].number, m.items[3].number, m.items[4].number, m.items[5].number,
                     m.items[6].number, m.items[7].number, m.items[8].number, m.items[9].number, m.items[10].number, m.items[11].number);
      }
      st->AddComponent(top, l, TopLoc_Location(tr));
    }
    if (assembly) st->UpdateAssemblies();
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

// ------------------------------------------------------------------------------------------------
// write-step (brief 69): the elaborated model as one STEP file
// ------------------------------------------------------------------------------------------------
//
// circuitRF decides WHAT is in the file (src/Design/ThreeD/Step/StepExport.cs): the parts, their names and colours,
// which higher-precedence parts each one loses its overlap to, and the assembly they sit in. This writes it. Every part
// arrives as resolved numbers in micrometres, in the WORLD frame, so the cuts are made where the solver makes them; a
// part a sub-assembly shares is then carried into that sub-assembly's own frame by its "local" matrix.

static gp_Trsf Matrix12(const Json& m, const NodeReader& r, const char* what)
{
  if (m.kind != Json::Array || m.items.size() != 12) r.Bad(std::string(what) + " is not twelve numbers (a 3 x 4 matrix, rows)");
  double v[12];
  for (int i = 0; i < 12; ++i)
  {
    if (!NodeReader::IsNum(m.items[i])) r.Bad(std::string(what) + " holds something that is not a finite number");
    v[i] = m.items[i].number;
  }
  gp_Trsf t;
  t.SetValues(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11]);
  return t;
}

// A ring as a closed wire: straight edges, or (smooth) one periodic spline through its points -- a round bond wire's
// section, as the .geo script's closed Spline draws it.
static TopoDS_Wire RingWire(const std::vector<gp_Pnt>& p, bool smooth, const NodeReader& r)
{
  if (!smooth) return Loop(p, nullptr, r);
  if (p.size() < 3) r.Bad("a ring has fewer than three points");
  occ::handle<TColgp_HArray1OfPnt> pts = new TColgp_HArray1OfPnt(1, static_cast<int>(p.size()));
  for (size_t i = 0; i < p.size(); ++i) pts->SetValue(static_cast<int>(i) + 1, p[i]);
  GeomAPI_Interpolate interp(pts, true, 1e-6);
  interp.Perform();
  if (!interp.IsDone()) r.Bad("a ring does not make a closed spline");
  return BRepBuilderAPI_MakeWire(BRepBuilderAPI_MakeEdge(interp.Curve()).Edge()).Wire();
}

// One part's shape, micrometres, in the world.
static TopoDS_Shape ExportGeometry(const Json& g, const std::string& name, const std::map<std::string, std::string>& blobs)
{
  NodeReader r{g, name};
  if (g.kind != Json::Object) r.Bad("a part's \"geometry\" is not an object");
  const Json& kind = r.Member("kind");
  if (kind.kind != Json::String) r.Bad("\"kind\" is not a string");
  const std::string& k = kind.text;
  auto holesOf = [&](const Json& node) {
    std::vector<std::vector<gp_Pnt>> holes;
    if (const Json* hs = node.Get("holes"))
    {
      if (hs->kind != Json::Array) r.Bad("\"holes\" is not an array of loops");
      for (const Json& h : hs->items) holes.push_back(r.Points(h, "a hole"));
    }
    return holes;
  };

  if (k == "box")
  {
    gp_XYZ a = r.Xyz("min"), b = r.Xyz("max");
    if (!(b.X() > a.X() && b.Y() > a.Y() && b.Z() > a.Z())) r.Bad("the box has no volume");
    return BRepPrimAPI_MakeBox(gp_Pnt(a), gp_Pnt(b)).Shape();
  }
  if (k == "cylinder")
  {
    gp_Pnt a(r.Xyz("from")), b(r.Xyz("to"));
    double radius = r.Num("radius");
    gp_Vec v(a, b);
    if (v.Magnitude() <= 0) r.Bad("the axis has no length");
    if (radius <= 0) r.Bad("the radius is not positive");
    return BRepPrimAPI_MakeCylinder(gp_Ax2(a, gp_Dir(v)), radius, v.Magnitude()).Shape();
  }
  if (k == "sphere")
  {
    gp_Pnt c(r.Xyz("centre"));
    double radius = r.Num("radius");
    if (radius <= 0) r.Bad("the radius is not positive");
    TopoDS_Shape s = BRepPrimAPI_MakeSphere(c, radius).Shape();
    // A ball flattened on its pad: the sphere kept between two heights, as the .geo script's intersection keeps it.
    double z0 = c.Z() - radius, z1 = c.Z() + radius;
    if (const Json* v = g.Get("zmin"); v && NodeReader::IsNum(*v)) z0 = std::max(z0, v->number);
    if (const Json* v = g.Get("zmax"); v && NodeReader::IsNum(*v)) z1 = std::min(z1, v->number);
    if (z0 <= c.Z() - radius && z1 >= c.Z() + radius) return s;
    if (!(z1 > z0)) r.Bad("the sphere is kept between heights that leave nothing");
    double w = 1.01 * radius;
    TopoDS_Shape slab = BRepPrimAPI_MakeBox(gp_Pnt(c.X() - w, c.Y() - w, z0), gp_Pnt(c.X() + w, c.Y() + w, z1)).Shape();
    BRepAlgoAPI_Common common(s, slab);
    if (!common.IsDone()) r.Bad("the truncated sphere could not be cut");
    return common.Shape();
  }
  if (k == "prism")
  {
    const Json& outline = r.Member("outline");
    size_t n = 2 + (outline.kind == Json::Array ? outline.items.size() : 0);
    for (auto& h : holesOf(g)) n += h.size();
    std::vector<std::string> names(n, "");
    return BuildPrism(r, names).shape;
  }
  if (k == "polyhedron")
  {
    const Json& loops = r.Member("loops");
    std::vector<std::string> names(loops.kind == Json::Array ? loops.items.size() : 0, "");
    return BuildPolyhedron(r, names).shape;
  }
  if (k == "loft")
  {
    // A bond wire: its section at every vertex of its path, joined vertex k to vertex k (a ruled loft, the .geo
    // script's Ruled ThruSections), closed by its two end faces.
    const Json& rings = r.Member("rings");
    if (rings.kind != Json::Array || rings.items.size() < 2) r.Bad("\"rings\" is not two or more rings");
    bool smooth = false;
    if (const Json* s = g.Get("smooth"); s && s->kind == Json::Bool) smooth = s->boolean;
    BRepFill_Generator gen;
    std::vector<TopoDS_Wire> wires;
    for (const Json& ring : rings.items)
    {
      wires.push_back(RingWire(r.Points(ring, "a ring"), smooth, r));
      gen.AddWire(wires.back());
    }
    gen.Perform();
    BRepBuilderAPI_Sewing sew(1e-4);
    sew.Add(gen.Shell());
    for (const TopoDS_Wire* w : {&wires.front(), &wires.back()})
    {
      BRepBuilderAPI_MakeFace cap(*w, true);
      if (!cap.IsDone()) r.Bad("an end of the wire is not flat");
      sew.Add(cap.Face());
    }
    sew.Perform();
    TopoDS_Shell shell;
    int shells = 0;
    for (TopExp_Explorer x(sew.SewedShape(), TopAbs_SHELL); x.More(); x.Next()) { shell = TopoDS::Shell(x.Current()); ++shells; }
    if (shells != 1) r.Bad("the wire's faces do not close into one shell");
    BRepBuilderAPI_MakeSolid ms(shell);
    if (!ms.IsDone()) r.Bad("the wire's shell does not bound a solid");
    return OrientedSolid(ms.Solid());
  }
  if (k == "face")
  {
    // A sheet: a surface, which a STEP file carries as a shell-based surface model.
    return PlanarFace(r.Points(r.Member("outline"), "outline"), holesOf(g), nullptr, nullptr, r);
  }
  if (k == "brep")
  {
    const Json& blob = r.Member("blob");
    if (blob.kind != Json::String) r.Bad("\"blob\" is not a blob's name");
    auto it = blobs.find(blob.text);
    if (it == blobs.end()) r.Bad("the request carries no blob '" + blob.text + "'");
    std::istringstream is(it->second);
    TopoDS_Shape s;
    BRep_Builder b;
    BRepTools::Read(s, is, b);
    if (s.IsNull()) r.Bad("the B-rep blob could not be read");
    return s;
  }
  r.Bad("a part cannot be a \"" + k + "\"");
}

static Frame OpWriteStep(const Json& req, const std::map<std::string, std::string>& blobs)
{
  const Json* parts = req.Get("parts");
  const Json* assemblies = req.Get("assemblies");
  if (parts == nullptr || parts->kind != Json::Array)
    throw Refuse{"request.malformed", "", "write-step needs \"parts\""};
  if (assemblies == nullptr || assemblies->kind != Json::Array || assemblies->items.empty())
    throw Refuse{"request.malformed", "", "write-step needs \"assemblies\": the root first"};
  std::string units = "mm";
  if (const Json* u = req.Get("units"); u && u->kind == Json::String) units = u->text;
  UnitsMethods_LengthUnit stepUnit = UnitsMethods_LengthUnit_Millimeter;
  MicronsPer(units, &stepUnit);

  // Test builds only: a write that takes as long as it is told, which is what cancelling an export is tested against.
  if (g_testOps)
    if (const char* s = std::getenv("CRF_GEOMETRY_WORKER_TEST_WRITE_SECONDS"))
      std::this_thread::sleep_for(std::chrono::duration<double>(std::atof(s)));

  // 1. Every part in the world.
  size_t n = parts->items.size();
  std::vector<std::string> names(n);
  std::vector<TopoDS_Shape> world(n);
  for (size_t i = 0; i < n; ++i)
  {
    const Json& p = parts->items[i];
    if (const Json* nm = p.Get("name"); nm && nm->kind == Json::String) names[i] = nm->text;
    const Json* g = p.Get("geometry");
    if (g == nullptr) throw Refuse{"request.malformed", names[i], "a part has no \"geometry\""};
    try
    {
      world[i] = ExportGeometry(*g, names[i], blobs);
    }
    catch (const Standard_Failure& e)
    {
      throw Refuse{"build.failed", names[i], std::string(e.ExceptionType()) + (e.what() && *e.what() ? std::string(": ") + e.what() : "")};
    }
  }

  // 2. Precedence (em-3d.md section 6.3a): each part loses what the parts its "cut" lists take -- cut from the parts as
  // built, never as already cut, which is the set the .geo script's cuts leave too.
  std::vector<TopoDS_Shape> final(world);
  std::vector<bool> empty(n, false);
  for (size_t i = 0; i < n; ++i)
  {
    const Json& p = parts->items[i];
    const Json* cut = p.Get("cut");
    if (cut == nullptr || cut->kind != Json::Array || cut->items.empty()) continue;
    NCollection_List<TopoDS_Shape> args, tools;
    args.Append(world[i]);
    for (const Json& j : cut->items)
    {
      if (j.kind != Json::Number || j.number < 0 || j.number >= double(n) || j.number == double(i))
        throw Refuse{"request.malformed", names[i], "\"cut\" names a part the request does not have"};
      tools.Append(world[static_cast<size_t>(j.number)]);
    }
    BRepAlgoAPI_Cut op;
    op.SetArguments(args);
    op.SetTools(tools);
    op.SetRunParallel(false);
    op.Build();
    if (op.HasErrors() || !op.IsDone())
    {
      std::ostringstream why;
      op.DumpErrors(why);
      throw Refuse{"export.failed", names[i], "taking the higher-precedence parts out of it failed: " + why.str()};
    }
    final[i] = op.Shape();
    empty[i] = !HasSolid(final[i]);
  }

  // 3. Into its sub-assembly's frame, where it has one.
  for (size_t i = 0; i < n; ++i)
  {
    const Json* local = parts->items[i].Get("local");
    if (empty[i] || local == nullptr || local->kind != Json::Array) continue;
    NodeReader r{parts->items[i], names[i]};
    BRepBuilderAPI_Transform xf(final[i], Matrix12(*local, r, "\"local\""), true);
    if (!xf.IsDone()) r.Bad("\"local\" could not be applied");
    final[i] = xf.Shape();
  }

  // 4. The document: one label per part, then the assemblies, each written once however often it is placed.
  occ::handle<TDocStd_Document> doc = NewXcafDocument();
  occ::handle<XCAFDoc_ShapeTool> st = XCAFDoc_DocumentTool::ShapeTool(doc->Main());
  occ::handle<XCAFDoc_ColorTool> ct = XCAFDoc_DocumentTool::ColorTool(doc->Main());
  std::vector<TDF_Label> labels(n);
  for (size_t i = 0; i < n; ++i)
  {
    if (empty[i]) continue;
    labels[i] = st->AddShape(final[i], false);
    TDataStd_Name::Set(labels[i], TCollection_ExtendedString(names[i].c_str(), true));
    const Json* c = parts->items[i].Get("colour");
    if (c && c->kind == Json::Array && c->items.size() == 4)
      // sRGB: a material's #rrggbb is what COLOUR_RGB then says. Quantity_TOC_RGB is LINEAR, and the writer re-encodes it, so
      // #b87333 went out as (0.866, 0.702, 0.485).
      ct->SetColor(labels[i], Quantity_ColorRGBA(Quantity_Color(c->items[0].number, c->items[1].number, c->items[2].number, Quantity_TOC_sRGB),
                                                 static_cast<float>(c->items[3].number)), XCAFDoc_ColorSurf);
  }
  std::map<size_t, TDF_Label> built;
  std::vector<bool> building(assemblies->items.size(), false);
  std::function<TDF_Label(size_t)> assembly = [&](size_t a) -> TDF_Label {
    if (auto it = built.find(a); it != built.end()) return it->second;
    if (building[a]) throw Refuse{"request.malformed", "", "an assembly contains itself"};
    building[a] = true;
    const Json& node = assemblies->items[a];
    NodeReader r{node, ""};
    TDF_Label lab = st->NewShape();
    if (const Json* nm = node.Get("name"); nm && nm->kind == Json::String)
      TDataStd_Name::Set(lab, TCollection_ExtendedString(nm->text.c_str(), true));
    const Json& comps = r.Member("components");
    if (comps.kind != Json::Array) r.Bad("\"components\" is not an array");
    for (const Json& c : comps.items)
    {
      NodeReader cr{c, ""};
      TDF_Label child;
      if (const Json* p = c.Get("part"))
      {
        if (p->kind != Json::Number || p->number < 0 || p->number >= double(n)) cr.Bad("\"part\" names a part the request does not have");
        size_t i = static_cast<size_t>(p->number);
        if (empty[i]) continue;
        child = labels[i];
      }
      else if (const Json* s = c.Get("assembly"))
      {
        if (s->kind != Json::Number || s->number < 0 || s->number >= double(assemblies->items.size()))
          cr.Bad("\"assembly\" names an assembly the request does not have");
        child = assembly(static_cast<size_t>(s->number));
      }
      else cr.Bad("a component names neither a \"part\" nor an \"assembly\"");
      gp_Trsf tr;
      if (const Json* l = c.Get("location"); l && l->kind == Json::Array) tr = Matrix12(*l, cr, "\"location\"");
      TDF_Label placed = st->AddComponent(lab, child, TopLoc_Location(tr));
      if (const Json* nm = c.Get("name"); nm && nm->kind == Json::String && !placed.IsNull())
        TDataStd_Name::Set(placed, TCollection_ExtendedString(nm->text.c_str(), true));
    }
    building[a] = false;
    return built[a] = lab;
  };
  assembly(0);
  st->UpdateAssemblies();

  // 5. The file. The header names the output's FILE (never its path), no author and no organisation, and the
  // originating system circuitRF states: a STEP file travels, and must not carry a login name or a home directory.
  STEPCAFControl_Writer wr;
  wr.SetNameMode(true);
  wr.SetColorMode(true);
  DESTEP_Parameters prm;
  prm.InitFromStatic();
  prm.WriteUnit = stepUnit;
  if (const Json* sc = req.Get("schema"); sc && sc->kind == Json::String && sc->text == "ap242")
    prm.WriteSchema = DESTEP_Parameters::WriteMode_StepSchema_AP242DIS;
  if (!wr.Transfer(doc, prm)) throw Refuse{"export.failed", "", "the STEP writer could not transfer the model"};
  // The same model is the same bytes whatever this worker wrote before: OCCT numbers each assembly occurrence's id from a
  // counter that lives as long as the process, so a second export from one worker would number its occurrences on from
  // the first's. Renumbered per file, in the model's own order, from 1.
  {
    occ::handle<StepData_StepModel> model = wr.ChangeWriter().Model();
    int next = 0;
    for (int i = 1; i <= model->NbEntities(); ++i)
      if (auto nauo = occ::down_cast<StepRepr_NextAssemblyUsageOccurrence>(model->Value(i)); !nauo.IsNull())
        nauo->SetId(new TCollection_HAsciiString(++next));
  }
  {
    APIHeaderSection_MakeHeader mh(wr.ChangeWriter().Model());
    auto text = [](const std::string& s) { return occ::handle<TCollection_HAsciiString>(new TCollection_HAsciiString(s.c_str())); };
    std::string fileName, description, system;
    if (const Json* h = req.Get("header"))
    {
      if (const Json* v = h->Get("name"); v && v->kind == Json::String) fileName = v->text;
      if (const Json* v = h->Get("description"); v && v->kind == Json::String) description = v->text;
      if (const Json* v = h->Get("system"); v && v->kind == Json::String) system = v->text;
    }
    mh.SetName(text(fileName));
    mh.SetAuthorValue(1, text(""));
    mh.SetOrganizationValue(1, text(""));
    mh.SetAuthorisation(text(""));
    mh.SetOriginatingSystem(text(system));
    mh.SetDescriptionValue(1, text(description));
  }
  std::ostringstream os;
  if (wr.WriteStream(os) != IFSelect_RetDone) throw Refuse{"export.failed", "", "the STEP writer did not complete"};
  std::string data = os.str();

  Reply rep;
  rep.j.Key("parts").BeginArr();
  for (size_t i = 0; i < n; ++i)
  {
    Counts c = empty[i] ? Counts{} : Count(final[i]);
    rep.j.Begin().Str("name", names[i]).Bool("empty", empty[i]).Int("solids", c.solids).Int("faces", c.faces)
         .Num("volume_um3", empty[i] ? 0.0 : Volume(final[i])).End();
  }
  rep.j.EndArr();
  rep.Blob("data", "bytes", data.size(), data);
  return rep.Finish();
}

// ------------------------------------------------------------------------------------------------
// reading a STEP file (brief 68): one entry per solid PART, located where its assembly puts it
// ------------------------------------------------------------------------------------------------
//
// ONE READER for `import-step` and the `step` tree node, so the dialog's table and every later build agree on the
// parts, their order, their faces and whether each is a solid. The XCAF document's length unit is the micrometre
// (NewXcafDocument), so the reader converts every coordinate from the FILE's unit as it transfers: exact for every
// SI unit and for the inch. A unit the reader cannot resolve is a refusal naming what the file says (R-em3d68-2b),
// never OCCT's silent default -- it would read an unknown unit as a millimetre.

// One solid of a part (brief 127), in TopExp_Explorer(part, TopAbs_SOLID) order after healing: what a Step node's
// "solid": k names, and what the import's table lists per row.
struct ReadSolid
{
  TopoDS_Shape shape;
  std::string name;      // the solid's own XCAF name, when the file gives one
  bool hasColor = false;
  bool mixed = false;    // no colour because its faces disagree (overview D6), not because nothing is coloured
  double rgb[3] = {0, 0, 0};
  bool closed = false;
  std::string why;
  int faces = 0;
  double volume = 0;     // um^3
  Box6 box{};            // um
};

struct ReadPart
{
  std::string name, path;
  bool hasColor = false;
  double rgb[3] = {0, 0, 0};
  TopoDS_Shape shape;
  bool closed = false;   // a closed solid after healing: what a Step object may be
  std::string why;       // why it is not, when it is not
  std::string healing;   // what healing changed, when it ran
  std::vector<ReadSolid> solids;
};

struct FileUnit { std::string name; double um = 0; };


struct StepRead
{
  std::vector<ReadPart> parts;
  std::vector<FileUnit> units;  // one per distinct length unit the file's representations state
  int pmi = 0;                  // dimensions, tolerances and datums the file carried (none is imported)
};

// A length unit's name as a person would say it: a conversion-based unit by the name the file gives it, an SI one
// by its prefix and "metre".
static std::string LengthUnitName(const occ::handle<StepBasic_NamedUnit>& u)
{
  if (occ::handle<StepBasic_ConversionBasedUnit> c = occ::down_cast<StepBasic_ConversionBasedUnit>(u); !c.IsNull())
  {
    std::string n = c->Name().IsNull() ? "" : c->Name()->ToCString();
    std::transform(n.begin(), n.end(), n.begin(), [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
    return n.empty() ? "an unnamed conversion-based unit" : n;
  }
  if (occ::handle<StepBasic_SiUnit> s = occ::down_cast<StepBasic_SiUnit>(u); !s.IsNull())
  {
    static const std::map<int, const char*> prefix = {
      {StepBasic_spKilo, "kilo"}, {StepBasic_spHecto, "hecto"}, {StepBasic_spDeca, "deca"}, {StepBasic_spDeci, "deci"},
      {StepBasic_spCenti, "centi"}, {StepBasic_spMilli, "milli"}, {StepBasic_spMicro, "micro"}, {StepBasic_spNano, "nano"}};
    std::string p;
    if (s->HasPrefix()) { auto it = prefix.find(s->Prefix()); p = it != prefix.end() ? it->second : "(a prefix)"; }
    return p + (s->Name() == StepBasic_sunMetre ? "metre" : "(not a length)");
  }
  return "an unrecognised unit";
}

static bool IsLengthUnit(const occ::handle<StepBasic_NamedUnit>& u)
{
  return u->IsKind(STANDARD_TYPE(StepBasic_SiUnitAndLengthUnit)) || u->IsKind(STANDARD_TYPE(StepBasic_ConversionBasedUnitAndLengthUnit))
         || u->IsKind(STANDARD_TYPE(StepBasic_LengthUnit));
}

// The file's length units, each resolved exactly as the reader's own transfer resolves it (STEPConstruct_UnitContext
// with the default factors, so LengthFactor() is millimetres per unit). Refuses when a representation states a length
// unit the reader cannot resolve, or when no representation states one at all.
static std::vector<FileUnit> LengthUnits(STEPCAFControl_Reader& rd)
{
  std::vector<FileUnit> units;
  occ::handle<StepData_StepModel> model = rd.ChangeReader().StepModel();
  if (model.IsNull()) return units;
  for (int i = 1; i <= model->NbEntities(); ++i)
  {
    occ::handle<StepRepr_GlobalUnitAssignedContext> ctx;
    occ::handle<Standard_Transient> e = model->Value(i);
    if (auto a = occ::down_cast<StepGeom_GeomRepContextAndGlobUnitAssCtxAndGlobUncertaintyAssCtx>(e); !a.IsNull())
      ctx = a->GlobalUnitAssignedContext();
    else if (auto b = occ::down_cast<StepGeom_GeometricRepresentationContextAndGlobalUnitAssignedContext>(e); !b.IsNull())
      ctx = b->GlobalUnitAssignedContext();
    if (ctx.IsNull()) continue;
    std::string name;
    for (int k = 1; k <= ctx->NbUnits(); ++k)
      if (occ::handle<StepBasic_NamedUnit> u = ctx->UnitsValue(k); !u.IsNull() && IsLengthUnit(u)) name = LengthUnitName(u);
    STEPConstruct_UnitContext uc;
    int status = uc.ComputeFactors(ctx, StepData_Factors());
    if (!uc.LengthDone() || !(uc.LengthFactor() > 0) || status == 3 || status == 11)
    {
      std::string because = status == 3    ? "its conversion factor is not stated in an SI unit"
                            : status == 11 ? "it names an SI unit that is not a length"
                                           : "no length factor follows from it";
      throw Refuse{"import.units", "",
                   name.empty() ? "the file states no length unit, and circuitRF does not guess one"
                                : "the file's length unit is '" + name + "', which the reader cannot resolve to a length (" + because
                                    + "); circuitRF does not guess one"};
    }
    double um = uc.LengthFactor() * 1000;  // millimetres per unit -> micrometres per unit
    bool seen = false;
    for (auto& u : units) seen = seen || (u.name == name && u.um == um);
    if (!seen) units.push_back({name.empty() ? "unnamed" : name, um});
  }
  if (units.empty()) throw Refuse{"import.units", "", "the file states no length unit, and circuitRF does not guess one"};
  return units;
}

// A part's shape, healed only when it fails the validity check: ShapeFix reports "done" for the tolerance touch-ups
// every translated file needs, which would make every report say every part was repaired. Returns what changed.
static std::string Heal(TopoDS_Shape& shape, occ::handle<ShapeBuild_ReShape>* context = nullptr)
{
  if (Valid(shape)) return "";
  ShapeAnalysis_ShapeTolerance tol;
  int facesBefore = Count(shape).faces;
  double tolBefore = tol.Tolerance(shape, 1);
  ShapeFix_Shape fix(shape);
  fix.Perform();
  shape = fix.Shape();
  if (context != nullptr) *context = fix.Context();
  std::vector<std::string> did;
  static const std::pair<ShapeExtend_Status, const char*> kinds[] = {
    {ShapeExtend_DONE1, "edges"}, {ShapeExtend_DONE2, "wires"}, {ShapeExtend_DONE3, "faces"},
    {ShapeExtend_DONE4, "shells"}, {ShapeExtend_DONE5, "solids"}};
  for (auto& [st, what] : kinds) if (fix.Status(st)) did.push_back(what);
  int facesAfter = Count(shape).faces;
  double tolAfter = tol.Tolerance(shape, 1);
  std::ostringstream o;
  o << "invalid as read; shape healing ";
  if (did.empty()) o << "ran";
  else
  {
    o << "fixed its ";
    for (size_t i = 0; i < did.size(); ++i) o << (i == 0 ? "" : i + 1 == did.size() ? " and " : ", ") << did[i];
  }
  if (facesAfter != facesBefore) o << ", faces " << facesBefore << " -> " << facesAfter;
  if (tolAfter > tolBefore * (1 + 1e-9)) o << ", largest tolerance raised " << tolBefore << " -> " << tolAfter << " um";
  o << (Valid(shape) ? "; the part is now valid" : "; the part is still invalid");
  return o.str();
}

// Whether a healed part is what a Step object may be -- a closed solid -- and if not, why (R-em3d68-4b).
static bool ClosedSolid(const TopoDS_Shape& s, std::string& why)
{
  if (!HasSolid(s))
  {
    bool shells = TopExp_Explorer(s, TopAbs_SHELL).More(), faces = TopExp_Explorer(s, TopAbs_FACE).More();
    why = shells ? "it is a surface model (shells with no solid)" : faces ? "it is loose faces, not a solid" : "it holds no geometry";
    return false;
  }
  for (TopExp_Explorer x(s, TopAbs_SHELL); x.More(); x.Next())
    if (!BRep_Tool::IsClosed(x.Current())) { why = "its shell is open, so it has no inside"; return false; }
  if (!Valid(s)) { why = "it is not a valid solid, even after shape healing"; return false; }
  return true;
}

// A label's colour, surface first, in the file's sRGB.
static bool LabelColour(const occ::handle<XCAFDoc_ColorTool>& ct, const TDF_Label& l, double rgb[3])
{
  Quantity_Color c;
  for (XCAFDoc_ColorType ty : {XCAFDoc_ColorSurf, XCAFDoc_ColorGen, XCAFDoc_ColorCurv})
    if (ct->GetColor(l, ty, c)) { c.Values(rgb[0], rgb[1], rgb[2], Quantity_TOC_sRGB); return true; }
  return false;
}

static bool SameColour(const double a[3], const double b[3])
{
  return std::abs(a[0] - b[0]) <= 1e-9 && std::abs(a[1] - b[1]) <= 1e-9 && std::abs(a[2] - b[2]) <= 1e-9;
}

// One solid's name and colour, looked up on the part's shape AS READ (`ref`'s, before it is located or healed: the
// sub-shape labels the reader made are keyed by those shapes). The colour is overview D6's, in its order: the solid's
// own; else the colour every face shares, a face with none counting as the part's; else the part's when no face
// carries one; else none -- marked mixed when the faces disagree. Never the colour covering most area.
static void DescribeSolid(const occ::handle<XCAFDoc_ShapeTool>& st, const occ::handle<XCAFDoc_ColorTool>& ct, const TDF_Label& ref,
                          const TopoDS_Shape& solid, const ReadPart& part, ReadSolid& out)
{
  TDF_Label l;
  bool labelled = st->FindSubShape(ref, solid, l);
  if (labelled)
  {
    occ::handle<TDataStd_Name> nm;
    if (l.FindAttribute(TDataStd_Name::GetID(), nm)) out.name = TCollection_AsciiString(nm->Get()).ToCString();
    if (LabelColour(ct, l, out.rgb)) { out.hasColor = true; return; }
  }
  bool anyFace = false, agree = true, first = true;
  double shared[3] = {0, 0, 0};
  Shapes faces;
  TopExp::MapShapes(solid, TopAbs_FACE, faces);
  for (int k = 1; k <= faces.Extent(); ++k)
  {
    double rgb[3];
    TDF_Label fl;
    bool has = st->FindSubShape(ref, faces(k), fl) && LabelColour(ct, fl, rgb);
    anyFace = anyFace || has;
    if (!has)
    {
      if (!part.hasColor) { agree = false; continue; }
      std::copy(part.rgb, part.rgb + 3, rgb);
    }
    if (first) { std::copy(rgb, rgb + 3, shared); first = false; }
    else if (!SameColour(rgb, shared)) agree = false;
  }
  if (!anyFace)
  {
    if (part.hasColor) { out.hasColor = true; std::copy(part.rgb, part.rgb + 3, out.rgb); }
    return;
  }
  if (agree && !first) { out.hasColor = true; std::copy(shared, shared + 3, out.rgb); return; }
  out.mixed = true;
}

// Walks the assembly tree, composing each occurrence's location with its parents' so a part in a sub-assembly lands
// where the whole file puts it. The occurrence path is the component index at each level, from 1.
static void Collect(const occ::handle<XCAFDoc_ShapeTool>& st, const occ::handle<XCAFDoc_ColorTool>& ct, const TDF_Label& l,
                    const std::string& path, const TopLoc_Location& parent, std::vector<ReadPart>& out)
{
  TDF_Label ref = l;
  if (st->IsReference(l)) st->GetReferredShape(l, ref);
  TopLoc_Location here = parent * XCAFDoc_ShapeTool::GetLocation(l);
  if (st->IsAssembly(ref))
  {
    NCollection_Sequence<TDF_Label> comps;
    st->GetComponents(ref, comps);
    for (int i = 1; i <= comps.Length(); ++i) Collect(st, ct, comps(i), path + "/" + std::to_string(i), here, out);
    return;
  }
  ReadPart p;
  p.path = path;
  occ::handle<TDataStd_Name> nm;
  if (ref.FindAttribute(TDataStd_Name::GetID(), nm) || l.FindAttribute(TDataStd_Name::GetID(), nm))
    p.name = TCollection_AsciiString(nm->Get()).ToCString();
  Quantity_Color c;
  for (TDF_Label q : {l, ref})
    for (XCAFDoc_ColorType ty : {XCAFDoc_ColorSurf, XCAFDoc_ColorGen, XCAFDoc_ColorCurv})
      if (!p.hasColor && ct->GetColor(q, ty, c)) { p.hasColor = true; c.Values(p.rgb[0], p.rgb[1], p.rgb[2], Quantity_TOC_sRGB); }
  TopoDS_Shape asRead = XCAFDoc_ShapeTool::GetShape(ref);
  p.shape = asRead.Moved(here);
  // sRGB: the file's own COLOUR_RGB numbers, which is what a material's #rrggbb is compared with (brief 69 found the reader
  // returning OCCT's LINEAR values, so a colour written by any other tool never matched by colour).
  if (!p.hasColor && ct->GetColor(p.shape, XCAFDoc_ColorSurf, c)) { p.hasColor = true; c.Values(p.rgb[0], p.rgb[1], p.rgb[2], Quantity_TOC_sRGB); }

  // brief 127: each solid's name and colour, from the shape as read, in its own solid order.
  std::vector<ReadSolid> described;
  std::vector<TopoDS_Shape> located;
  TopExp_Explorer moved(p.shape, TopAbs_SOLID);
  for (TopExp_Explorer x(asRead, TopAbs_SOLID); x.More(); x.Next(), moved.Next())
  {
    ReadSolid d;
    DescribeSolid(st, ct, ref, x.Current(), p, d);
    described.push_back(d);
    located.push_back(moved.Current());
  }

  occ::handle<ShapeBuild_ReShape> context;
  p.healing = Heal(p.shape, &context);
  p.closed = ClosedSolid(p.shape, p.why);

  // The solids after healing, which is the order "solid": k counts in. Healing that kept the count kept the order (it
  // rebuilds a compound in place); one that did not is followed through its own record of what replaced what.
  std::vector<TopoDS_Shape> healed;
  for (TopExp_Explorer x(p.shape, TopAbs_SOLID); x.More(); x.Next()) healed.push_back(x.Current());
  for (size_t k = 0; k < healed.size(); ++k)
  {
    ReadSolid s;
    int from = healed.size() == described.size() ? static_cast<int>(k) : -1;
    if (from < 0 && !context.IsNull())
      for (size_t i = 0; i < located.size() && from < 0; ++i)
        if (context->Value(located[i]).IsSame(healed[k])) from = static_cast<int>(i);
    if (from >= 0) s = described[from];
    s.shape = healed[k];
    s.closed = ClosedSolid(s.shape, s.why);
    s.faces = Count(s.shape).faces;
    s.volume = Volume(s.shape);
    s.box = Tight(s.shape);
    p.solids.push_back(s);
  }
  out.push_back(p);
}

// Reads a STEP file from bytes (import-step) or a path (a build's step node).
static StepRead ReadStep(const std::string* bytes, const std::string* path)
{
  STEPCAFControl_Reader rd;
  rd.SetNameMode(true);
  rd.SetColorMode(true);
  rd.SetGDTMode(true);
  IFSelect_ReturnStatus status;
  if (bytes != nullptr)
  {
    std::istringstream is(*bytes);
    status = rd.ReadStream("import.step", is);
  }
  else status = rd.ReadFile(path->c_str());
  ++g_stepReads;
  std::string what = path != nullptr ? "'" + *path + "'" : "the file";
  if (status != IFSelect_RetDone) throw Refuse{"import.failed", "", what + " is not a STEP file the reader can read"};
  // brief 127: a solid's own name (MANIFOLD_SOLID_BREP('name',...)) lands on its sub-shape label. Off by default in OCCT;
  // it adds labels and names only, never changes a shape.
  if (occ::handle<StepData_StepModel> m = rd.ChangeReader().StepModel(); !m.IsNull()) m->InternalParameters.ReadSubshapeNames = true;

  StepRead out;
  out.units = LengthUnits(rd);
  occ::handle<TDocStd_Document> doc = NewXcafDocument();
  if (!rd.Transfer(doc)) throw Refuse{"import.failed", "", "the STEP reader could not transfer the shapes of " + what};
  occ::handle<XCAFDoc_ShapeTool> st = XCAFDoc_DocumentTool::ShapeTool(doc->Main());
  occ::handle<XCAFDoc_ColorTool> ct = XCAFDoc_DocumentTool::ColorTool(doc->Main());
  NCollection_Sequence<TDF_Label> free;
  st->GetFreeShapes(free);
  for (int i = 1; i <= free.Length(); ++i) Collect(st, ct, free(i), std::to_string(i), TopLoc_Location(), out.parts);
  if (XCAFDoc_DocumentTool::CheckDimTolTool(doc->Main()))
  {
    occ::handle<XCAFDoc_DimTolTool> dt = XCAFDoc_DocumentTool::DimTolTool(doc->Main());
    NCollection_Sequence<TDF_Label> dims, tols, datums;
    dt->GetDimensionLabels(dims);
    dt->GetGeomToleranceLabels(tols);
    dt->GetDatumLabels(datums);
    out.pmi = dims.Length() + tols.Length() + datums.Length();
  }
  return out;
}

static std::string UnitsText(const std::vector<FileUnit>& units)
{
  std::string s;
  for (size_t i = 0; i < units.size(); ++i) s += (i == 0 ? "" : ", ") + units[i].name;
  return s;
}

// import-step: every part, as the dialog's table lists it. With "display_rel" (a fraction of each part's diagonal) it
// also counts the triangles the viewport would draw the part with (R-em3d68-4c). "hold": false holds nothing.
static Frame OpImportStep(const Json& req, const std::map<std::string, std::string>& blobs)
{
  const Json* prefix = req.Get("shape");
  if (prefix == nullptr || prefix->kind != Json::String || prefix->text.empty())
    throw Refuse{"request.malformed", "", "import-step needs \"shape\": the handle prefix its parts are held under"};
  bool hold = true;
  if (const Json* h = req.Get("hold"); h && h->kind == Json::Bool) hold = h->boolean;
  double displayRel = 0;
  if (const Json* d = req.Get("display_rel"); d && d->kind == Json::Number && d->number > 0) displayRel = d->number;

  // Test builds only: a read that takes as long as it is told, which is what cancellation is tested against.
  if (g_testOps)
    if (const char* s = std::getenv("CRF_GEOMETRY_WORKER_TEST_IMPORT_SECONDS"))
      std::this_thread::sleep_for(std::chrono::duration<double>(std::atof(s)));

  StepRead read;
  auto file = blobs.find("file");
  if (file != blobs.end()) read = ReadStep(&file->second, nullptr);
  else if (const Json* p = req.Get("path"); p && p->kind == Json::String) read = ReadStep(nullptr, &p->text);
  else throw Refuse{"request.malformed", "", "import-step needs the file as a \"file\" blob or a \"path\""};

  Reply r;
  r.j.Key("units").BeginArr();
  for (auto& u : read.units) r.j.Str(u.name);
  r.j.EndArr();
  r.j.Key("unit_um").BeginArr();
  for (auto& u : read.units) r.j.Num(u.um);
  r.j.EndArr();
  r.j.Int("pmi", read.pmi);
  std::vector<std::string> healing;
  r.j.Key("parts").BeginArr();
  for (size_t i = 0; i < read.parts.size(); ++i)
  {
    ReadPart& p = read.parts[i];
    if (!p.healing.empty()) healing.push_back("part " + p.path + (p.name.empty() ? "" : " '" + p.name + "'") + ": " + p.healing);
    std::string handle = prefix->text + "/" + std::to_string(i + 1);
    if (hold)
    {
      Held h{p.shape, {}};
      Shapes faces;
      TopExp::MapShapes(p.shape, TopAbs_FACE, faces);
      for (int k = 1; k <= faces.Extent(); ++k) h.faceNames.push_back("face" + std::to_string(k));
      g_shapes[handle] = h;
    }
    Counts c = Count(p.shape);
    long long triangles = 0;
    if (displayRel > 0 && c.faces > 0)
    {
      Box6 b = Tight(p.shape);
      double diag = std::sqrt((b.x1 - b.x0) * (b.x1 - b.x0) + (b.y1 - b.y0) * (b.y1 - b.y0) + (b.z1 - b.z0) * (b.z1 - b.z0));
      triangles = static_cast<long long>(Tessellate(p.shape, std::max(diag * displayRel, 1e-3), 0.5).faceOfTri.size());
    }
    r.j.Begin().Str("shape", hold ? handle : "").Str("name", p.name).Str("path", p.path);
    r.j.Key("colour");
    if (p.hasColor) r.j.BeginArr().Num(p.rgb[0]).Num(p.rgb[1]).Num(p.rgb[2]).EndArr();
    else r.j.Null();
    r.j.Int("faces", c.faces).Bool("valid", Valid(p.shape)).Bool("closed", p.closed).Str("why", p.why)
       .Str("healing", p.healing).Int("triangles", triangles);
    // brief 127: each solid, as "solid": k would build it. Its length is the part's solid count (Count's own explorer).
    r.j.Key("solids").BeginArr();
    for (const ReadSolid& s : p.solids)
    {
      long long st = 0;
      if (displayRel > 0 && s.faces > 0)
      {
        const Box6& b = s.box;
        double diag = std::sqrt((b.x1 - b.x0) * (b.x1 - b.x0) + (b.y1 - b.y0) * (b.y1 - b.y0) + (b.z1 - b.z0) * (b.z1 - b.z0));
        st = static_cast<long long>(Tessellate(s.shape, std::max(diag * displayRel, 1e-3), 0.5).faceOfTri.size());
      }
      r.j.Begin().Str("name", s.name).Key("colour");
      if (s.hasColor) r.j.BeginArr().Num(s.rgb[0]).Num(s.rgb[1]).Num(s.rgb[2]).EndArr();
      else r.j.Null();
      r.j.Bool("mixed", s.mixed).Int("faces", s.faces).Bool("closed", s.closed).Str("why", s.why).Num("volume_um3", s.volume);
      r.j.Key("box_um").BeginArr().Num(s.box.x0).Num(s.box.y0).Num(s.box.z0).Num(s.box.x1).Num(s.box.y1).Num(s.box.z1).EndArr();
      r.j.Int("triangles", st).End();
    }
    r.j.EndArr();
    r.j.End();
  }
  r.j.EndArr();
  r.j.Key("healing").BeginArr();
  for (auto& s : healing) r.j.Str(s);
  r.j.EndArr();
  return r.Finish();
}


// ------------------------------------------------------------------------------------------------
// a Step node (brief 64): one solid part of a STEP file, located where its assembly puts it
// ------------------------------------------------------------------------------------------------

// A STEP file's parts, read once per file (by its path and the hash the document recorded) for the
// life of the worker: a document with several parts of one file reads it once.
static std::map<std::string, StepRead> g_stepFiles;

// g_buildNotes (declared above BuildNode): what a build's step nodes add to its reply -- healing -- cleared by each build.

static Named BuildStep(const NodeReader& r)
{
  const Json& file = r.Member("file");
  const Json& part = r.Member("part");
  if (file.kind != Json::String || file.text.empty()) r.Bad("\"file\" is not a path");
  if (part.kind != Json::String || part.text.empty()) r.Bad("\"part\" is not an occurrence path");
  std::string hash;
  if (const Json* h = r.node.Get("hash"); h && h->kind == Json::String) hash = h->text;
  // brief 127: "solid": k, 1-based in the part's solid order after healing; absent for the whole part.
  long long solid = 0;
  if (const Json* sv = r.node.Get("solid"))
  {
    if (!NodeReader::IsNum(*sv) || sv->number < 1 || sv->number != std::floor(sv->number) || sv->number > 1e9)
      r.Bad("\"solid\" is not a whole number of at least 1");
    solid = static_cast<long long>(sv->number);
  }
  std::string key = file.text + "\n" + hash;
  auto it = g_stepFiles.find(key);
  if (it == g_stepFiles.end())
  {
    try
    {
      it = g_stepFiles.emplace(key, ReadStep(nullptr, &file.text)).first;
    }
    catch (Refuse& e)
    {
      e.object = r.name;
      throw;
    }
  }
  for (const ReadPart& p : it->second.parts)
  {
    if (p.path != part.text) continue;
    if (solid > 0)
    {
      size_t n = p.solids.size();
      if (static_cast<size_t>(solid) > n)
        r.Bad("the part has " + std::to_string(n) + (n == 1 ? " solid" : " solids") + "; there is no solid " + std::to_string(solid));
      const ReadSolid& s = p.solids[solid - 1];
      std::string which = "solid " + std::to_string(solid) + " of part " + part.text;
      if (!s.closed) r.Bad(which + " is not a closed solid: " + s.why);
      if (!p.healing.empty()) g_buildNotes.push_back("part " + part.text + ", solid " + std::to_string(solid) + ": " + p.healing);
      // Faces are face<n> in THIS solid's own topological order (R-em3d68-1e, applied to the solid).
      Named n1{s.shape, {}, gp_Trsf()};
      Shapes faces;
      TopExp::MapShapes(s.shape, TopAbs_FACE, faces);
      for (int k = 1; k <= faces.Extent(); ++k) n1.faces.push_back({faces(k), "face" + std::to_string(k)});
      return n1;
    }
    if (!p.closed) r.Bad("part " + part.text + " of the file is not a closed solid: " + p.why);
    if (!p.healing.empty()) g_buildNotes.push_back("part " + part.text + ": " + p.healing);
    // Faces are face<n> in the part's own topological order (brief 68 R-em3d68-1e), as import-step names them.
    Named n{p.shape, {}, gp_Trsf()};
    Shapes faces;
    TopExp::MapShapes(p.shape, TopAbs_FACE, faces);
    for (int k = 1; k <= faces.Extent(); ++k) n.faces.push_back({faces(k), "face" + std::to_string(k)});
    return n;
  }
  r.Bad("the file has no part " + part.text);
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
    if (name == "loops") return OpLoops(req);
    if (name == "export") return OpExport(req);
    if (name == "write-step") return OpWriteStep(req, blobs);
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
