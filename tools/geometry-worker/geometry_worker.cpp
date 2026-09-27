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
//  THE PROTOCOL (brief-em3d-62 R-em3d62-2 -- the skeleton; brief 63 extends it). Line-delimited JSON:
//  one request per line on stdin, one response per line on stdout, diagnostics on stderr.
//
//      {"op":"hello","protocol":1}        {"ok":true,"worker":"<VERSION>","occt":"8.0.1","protocol":1,"modules":[...]}
//      {"op":"box","size_um":[x,y,z]}     {"ok":true,"solids":1,"faces":6,"volume_um3":...}
//      {"op":"selftest"}                  {"ok":true,"faces":8,"valid":true,"step_roundtrip":true,...}
//      {"op":"quit"}                      {"ok":true}, then exit 0
//      anything else                      {"ok":false,"error":"<sentence>"} -- and the worker keeps running
//
//  `geometry-worker --version` prints the worker's version and the OCCT version it loaded, and exits 0
//  (1 when the loaded OCCT is not the one it was built against).
//
//  FAILURE (brief 61 Q9, docs/design/em-3d-f4b-spike-findings.md "Crash posture"). A C++ exception --
//  Standard_Failure or std::exception -- refuses that one request and the worker carries on. A fault
//  inside the kernel is turned into an exception by OSD::SetSignal (Q9 found the process usable
//  afterwards), but a handler cannot vouch for a heap after a wild write: the worker answers that
//  request with a refusal and then EXITS (status 3), and the client restarts it (40 ms, Q12).
//  Cancellation is always by killing the process -- a fillet never polls a user break.
// ================================================================================================

#include <BRepAlgoAPI_Cut.hxx>
#include <BRepBndLib.hxx>
#include <BRepCheck_Analyzer.hxx>
#include <BRepFilletAPI_MakeFillet.hxx>
#include <BRepGProp.hxx>
#include <BRepPrimAPI_MakeBox.hxx>
#include <BRepPrimAPI_MakeCylinder.hxx>
#include <Bnd_Box.hxx>
#include <DESTEP_Parameters.hxx>
#include <GProp_GProps.hxx>
#include <Message.hxx>
#include <Message_Messenger.hxx>
#include <Message_PrinterOStream.hxx>
#include <NCollection_IndexedMap.hxx>
#include <OSD.hxx>
#include <OSD_Signal.hxx>
#include <Quantity_Color.hxx>
#include <STEPCAFControl_Reader.hxx>
#include <STEPCAFControl_Writer.hxx>
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
#include <XCAFApp_Application.hxx>
#include <XCAFDoc_ColorTool.hxx>
#include <XCAFDoc_DocumentTool.hxx>
#include <XCAFDoc_ShapeTool.hxx>
#include <gp_Ax2.hxx>

#include <cmath>
#include <cstdio>
#include <cstring>
#include <exception>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
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

// What this build can do, by feature rather than by toolkit. brief 63 reads it; the skeleton only
// reports it.
static const char* const kModules[] = {"primitives", "booleans", "fillets", "shape-healing", "mesh", "step"};

// The protocol's own stream. Everything else anyone prints -- OCCT included -- lands on stderr: at
// start-up fd 1 is duplicated for the protocol and then pointed at fd 2, so a stray printf deep in a
// translator can never corrupt a response.
static FILE* g_proto = stdout;

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

// A response, built member by member.
class Reply
{
public:
  explicit Reply(bool ok) { s_ = std::string("{\"ok\":") + (ok ? "true" : "false"); }

  Reply& Str(const char* key, const std::string& v)  { Key(key); Quote(v); return *this; }
  Reply& Int(const char* key, long long v)           { Key(key); s_ += std::to_string(v); return *this; }
  Reply& Bool(const char* key, bool v)               { Key(key); s_ += v ? "true" : "false"; return *this; }
  Reply& Num(const char* key, double v)
  {
    Key(key);
    if (!std::isfinite(v)) { s_ += "null"; return *this; }
    char b[40];
    std::snprintf(b, sizeof b, "%.17g", v);
    s_ += b;
    return *this;
  }
  Reply& List(const char* key, const char* const* v, size_t n)
  {
    Key(key);
    s_ += '[';
    for (size_t i = 0; i < n; ++i) { if (i) s_ += ','; Quote(v[i]); }
    s_ += ']';
    return *this;
  }

  std::string Line() const { return s_ + "}"; }

private:
  std::string s_;

  void Key(const char* k) { s_ += ",\""; s_ += k; s_ += "\":"; }

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

static std::string Refusal(const std::string& sentence) { return Reply(false).Str("error", sentence).Line(); }

static void Send(const std::string& line)
{
  std::fputs(line.c_str(), g_proto);
  std::fputc('\n', g_proto);
  std::fflush(g_proto);
}

// ------------------------------------------------------------------------------------------------
// The OCCT this process LOADED, against the one it was BUILT against (R-em3d62-2a)
// ------------------------------------------------------------------------------------------------

static std::string LoadedOcct() { return OCCT_Version_String_Complete(); }

static bool OcctMatches() { return LoadedOcct() == OCC_VERSION_COMPLETE; }

static std::string MismatchSentence()
{
  return "this geometry worker was built against Open CASCADE Technology " OCC_VERSION_COMPLETE
         " but loaded " + LoadedOcct() + "; its libraries are not the ones it shipped with. "
         "Reinstall circuitRF, or rebuild the kernel with tools/geometry-worker/build.sh";
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
  NCollection_IndexedMap<TopoDS_Shape, TopTools_ShapeMapHasher> faces;
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

static bool Valid(const TopoDS_Shape& s) { return !s.IsNull() && BRepCheck_Analyzer(s).IsValid(); }

// ------------------------------------------------------------------------------------------------
// the requests
// ------------------------------------------------------------------------------------------------

static std::string OpHello(const Json& req)
{
  if (const Json* p = req.Get("protocol"))
    if (p->kind != Json::Number || p->number != kProtocol)
      return Refusal("this geometry worker speaks protocol " + std::to_string(kProtocol) + " only");
  return Reply(true)
    .Str("worker", CRF_WORKER_VERSION)
    .Str("occt", LoadedOcct())
    .Int("protocol", kProtocol)
    .List("modules", kModules, sizeof kModules / sizeof kModules[0])
    .Line();
}

static std::string OpBox(const Json& req)
{
  const Json* size = req.Get("size_um");
  if (size == nullptr || size->kind != Json::Array || size->items.size() != 3)
    return Refusal("box needs \"size_um\": three lengths in micrometres, [x, y, z]");
  double d[3];
  for (int i = 0; i < 3; ++i)
  {
    const Json& v = size->items[i];
    if (v.kind != Json::Number || !std::isfinite(v.number) || v.number <= 0)
      return Refusal("box needs three positive, finite lengths in micrometres");
    d[i] = v.number;
  }
  TopoDS_Shape box = BRepPrimAPI_MakeBox(d[0], d[1], d[2]).Shape();
  if (!Valid(box)) return Refusal("the kernel built a box that does not pass its own validity check");
  Counts c = Count(box);
  return Reply(true).Int("solids", c.solids).Int("faces", c.faces).Num("volume_um3", Volume(box)).Line();
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

static occ::handle<TDocStd_Document> NewXcafDocument()
{
  occ::handle<TDocStd_Document> doc;
  XCAFApp_Application::GetApplication()->NewDocument("MDTV-XCAF", doc);
  XCAFDoc_DocumentTool::SetLengthUnit(doc, 1e-6);
  return doc;
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

static std::string OpSelftest()
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

  bool ok = valid && cutOk && filOk && step;
  Reply reply(ok);
  if (!ok)
  {
    std::string what = !valid ? "a result failed the kernel's validity check"
                     : !cutOk ? "the block minus the cylinder came out with the wrong faces or volume"
                     : !filOk ? "the filleted rim came out with the wrong faces or volume"
                              : "the part did not survive a STEP write and read back in memory";
    reply.Str("error", "the geometry kernel's self-test failed: " + what);
  }
  return reply.Int("faces", fc.faces)
    .Bool("valid", valid)
    .Bool("step_roundtrip", step)
    .Num("volume_um3", vFil)
    .Num("volume_ref_um3", vFilRef)
    .Str("occt", LoadedOcct())
    .Line();
}

// One request. `quit` is set when the worker should exit after answering.
static std::string Dispatch(const std::string& line, bool& quit)
{
  Json req;
  try
  {
    req = JsonReader(line).Document();
  }
  catch (const std::exception& e)
  {
    return Refusal(std::string("the request is not valid JSON: ") + e.what());
  }
  const Json* op = req.Get("op");
  if (req.kind != Json::Object || op == nullptr || op->kind != Json::String)
    return Refusal("a request is a JSON object with an \"op\" member naming the operation");

  const std::string& name = op->text;
  if (name == "quit") { quit = true; return Reply(true).Line(); }
  if (!OcctMatches()) return Refusal(MismatchSentence());
  if (name == "hello") return OpHello(req);
  if (name == "box") return OpBox(req);
  if (name == "selftest") return OpSelftest();
  return Refusal("this geometry worker does not know the operation \"" + name + "\"");
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
                 "usage: geometry-worker            speak the protocol on stdin/stdout\n"
                 "       geometry-worker --version  print the worker and OCCT versions\n");
    return 2;
  }

  // The protocol gets its own descriptor; fd 1 then points at stderr for everyone else.
  std::fflush(stdout);
  int protoFd = crf_dup(crf_fileno(stdout));
  if (protoFd >= 0 && crf_dup2(crf_fileno(stderr), crf_fileno(stdout)) >= 0)
    if (FILE* f = crf_fdopen(protoFd, "w")) g_proto = f;
#if defined(_WIN32)
  _setmode(crf_fileno(g_proto), _O_BINARY);  // "\n", never "\r\n"
  _setmode(crf_fileno(stdin), _O_BINARY);
#endif

  // OCCT's default messenger prints to standard output; with fd 1 on stderr that is now harmless, and
  // removing it keeps the diagnostics stream quiet too.
  Message::DefaultMessenger()->RemovePrinters(STANDARD_TYPE(Message_PrinterOStream));

  // A fault inside the kernel becomes an OSD_Signal exception rather than a dead process (Q9).
  OSD::SetSignal(false);

  std::string line;
  while (std::getline(std::cin, line))
  {
    if (!line.empty() && line.back() == '\r') line.pop_back();
    if (line.find_first_not_of(" \t") == std::string::npos) continue;

    bool quit = false, fatal = false;
    std::string response;
    try
    {
      OCC_CATCH_SIGNALS
      response = Dispatch(line, quit);
    }
    catch (const OSD_Signal& e)
    {
      std::string msg = e.what() ? e.what() : "";
      response = Refusal(std::string("the geometry kernel faulted (") + e.ExceptionType()
                         + (msg.empty() ? "" : ": " + msg) + "); the worker will restart");
      fatal = true;
    }
    catch (const Standard_Failure& e)
    {
      std::string msg = e.what() ? e.what() : "";
      response = Refusal(std::string("the geometry kernel refused the operation (") + e.ExceptionType()
                         + (msg.empty() ? "" : ": " + msg) + ")");
    }
    catch (const std::exception& e)
    {
      response = Refusal(std::string("the geometry worker failed: ") + e.what());
    }
    catch (...)
    {
      response = Refusal("the geometry worker failed with an unknown exception");
    }

    Send(response);
    if (fatal)
    {
      std::fprintf(stderr, "geometry-worker: exiting after a caught signal; the heap cannot be trusted\n");
      return 3;
    }
    if (quit) return 0;
  }
  return 0;
}
