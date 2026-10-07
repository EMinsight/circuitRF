// ================================================================================================
//  gdstk-worker -- reads and writes GDSII and OASIS through gdstk, out of process
//
//  Copyright (c) circuitRF contributors. Released under the MIT License (see LICENSE at the root of
//  the circuitRF repository).
//
//  This program uses gdstk (Boost Software License 1.0), qhull (the Qhull licence) and zlib (the zlib
//  licence), linked statically. Nothing of theirs is copied into this file or into this directory.
//
//  brief-oasis-gdstk.md §4-§5 (R-oas-1); grown from the G0 spike's worker, whose findings
//  (docs/design/oasis-gdstk-findings.md, "Worker changes the spike forced") every part below keeps.
//  Frames exactly as the geometry worker's:
//
//      [uint32 jsonLen][uint32 binLen][jsonLen bytes of UTF-8 JSON][binLen bytes]      little-endian
//
//  with the binary part declared by the JSON's "blobs" array in order ({"name","type","count"}, type
//  f64 / u32 / bytes). A refusal is an ordinary reply, {"ok":false,"code":...,"detail":...}, and the
//  worker keeps running. Diagnostics -- gdstk's own error_logger included -- go to stderr. A frame
//  whose header announces more than kMaxJson / kMaxBin bytes is answered with "frame.too-large" and the
//  worker EXITS (status 4): the stream is out of step, and waiting for the bytes would be a hang.
//
//      hello         protocol -> worker, gdstk, qhull, zlib, protocol, rid (+ code_page on Windows)
//      open          path, format (gds|oas), tolerance_dbu -> handle, unit_m, precision_m, cells[],
//                    layer_names[], messages[]
//      cell          handle, name -> one cell (below), coordinates in the file's database units
//      close         handle
//      begin-write   unit_m, precision_m, format, options -> handle
//      add-cell      handle, one cell (below)
//      finish-write  handle, path -> bytes, cells, messages[]   (written to a temporary name, then renamed)
//      shutdown      -> ok, then exit 0
//      selftest      a fixed round trip through both formats, for tools/CliSmoke
//
//  A CELL, both directions:
//      {"name", "polygons":[{layer,datatype,n,rep?}], "paths":[{layer,datatype,width,end,ext?,n,rep?}],
//       "labels":[{layer,texttype,text,x,y,anchor,rotation,magnification,mirror,rep?}],
//       "refs":[{cell,x,y,rotation,magnification,mirror,rep?}],
//       blobs: "xy" f64 (every polygon's vertices, in order), "path_xy" f64 (every path's spine)}
//  Rotation in degrees. A repetition, as gdstk HOLDS it (it has five kinds for OASIS's eleven):
//      {"kind":"rectangular","columns","rows","spacing":[x,y]} | {"kind":"regular","columns","rows",
//       "v1":[x,y],"v2":[x,y]} | {"kind":"explicit","offsets":[x,y,...]} | {"kind":"explicit_x"|
//       "explicit_y","coords":[...]}
//
//  COORDINATES. Every coordinate in a reply is the file's own integer database unit, carried as a
//  double that IS an integer. A GDSII file is read with gdstk's unit set to the file's own precision, so
//  gdstk's scale factor is exactly 1.0. An OASIS file is read the same way, but there the factor is
//  1 +- 1 ulp, and 566,644 of 10^6 values arrived off by ulps (findings Q6): every coordinate, width,
//  extension and repetition value is rounded to the nearest integer (half away from zero) before it is sent, and a
//  value off its grid by more than ulps -- a CIRCLE's vertices -- is counted in the cell's notes.
//
//  TEST SWITCH. With CRF_GDSTK_WORKER_TEST=1 in its environment the worker also takes "validate":false on
//  an OASIS open (the G0 spike's Q3 counts what gdstk does unguarded), and the ops "crash" (exit 70,
//  answering nothing) and "sleep" (seconds) -- what the client's crash and timeout gates are tested
//  against. Without it, all three are refused.
// ================================================================================================

#include <gdstk/gdstk.hpp>
#include <zlib.h>

#include <cerrno>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <ctime>
#include <map>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

#if defined(_WIN32)
  // GetTempPathW / GetTempFileNameW, GetFullPathNameW, MoveFileExW / DeleteFileW and the UTF-8 <-> UTF-16
  // conversions only. NOGDI keeps wingdi's Polygon() away from gdstk::Polygon.
  #define WIN32_LEAN_AND_MEAN
  #define NOGDI
  #define NOMINMAX
  #include <windows.h>
  #include <fcntl.h>
  #include <io.h>
  #define crf_dup _dup
  #define crf_dup2 _dup2
  #define crf_fdopen _fdopen
  #define crf_fileno _fileno
  #define crf_fseek64 _fseeki64
  #define crf_ftell64 _ftelli64
  #define crf_getpid _getpid
  #include <process.h>
#else
  #include <unistd.h>
  #define crf_dup dup
  #define crf_dup2 dup2
  #define crf_fdopen fdopen
  #define crf_fileno fileno
  #define crf_fseek64 fseeko
  #define crf_ftell64 ftello
  #define crf_getpid getpid
#endif

#ifndef CRF_WORKER_VERSION
  #define CRF_WORKER_VERSION "dev"
#endif

using namespace gdstk;

// qhull's own version string (libqhull_r/global_r.c). Declared here rather than by including
// libqhull_r.h, whose macros (qh, boolT, ...) have no business in this file.
extern "C" const char qh_version[];

static const int kProtocol = 1;
// The largest frame parts the worker will wait for. The JSON of the largest cell the G0 spike measured
// (10^6 polygons in 100 cells) was 1.27 MB a frame; a single cell's vertex blob grows by 16 bytes a vertex.
static const uint32_t kMaxJson = 64u << 20;          // 64 MB
static const uint32_t kMaxBin = 2047u << 20;         // just under 2 GB

static bool TestSwitch()
{
  const char* t = std::getenv("CRF_GDSTK_WORKER_TEST");
  return t != nullptr && std::strcmp(t, "1") == 0;
}
static const double kPi = 3.14159265358979323846;

static const char* CompiledRid()
{
#if defined(_WIN32)
  #if defined(_M_ARM64) || defined(__aarch64__)
  return "win-arm64";
  #elif defined(_M_X64) || defined(__x86_64__)
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

static FILE* g_proto = stdout;

// ------------------------------------------------------------------------------------------------
// JSON -- the geometry worker's reader and writer (tools/geometry-worker/geometry_worker.cpp)
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
    if (i_ >= t_.size()) Fail("the text ends where a value was expected");
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
    ++i_;
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

  // Names in a GDSII or OASIS file are bytes, and a file from another tool (or a damaged one) may carry
  // bytes that are not UTF-8. A reply is always valid UTF-8 JSON: a byte that does not begin a valid UTF-8
  // sequence is written as the Latin-1 code point it would be (\u00XX), so the client can always read the
  // reply and the name survives as a (different) string rather than stopping the conversation.
  static size_t Utf8Len(const std::string& v, size_t i)
  {
    unsigned char c = (unsigned char)v[i];
    size_t n = c < 0x80 ? 1 : (c >> 5) == 0x6 ? 2 : (c >> 4) == 0xE ? 3 : (c >> 3) == 0x1E ? 4 : 0;
    if (n == 0 || i + n > v.size()) return 0;
    for (size_t k = 1; k < n; k++)
      if (((unsigned char)v[i + k] >> 6) != 0x2) return 0;
    return n;
  }

  void Quote(const std::string& v)
  {
    s_ += '"';
    for (size_t i = 0; i < v.size(); i++)
    {
      unsigned char c = (unsigned char)v[i];
      if (c >= 0x80)
      {
        size_t n = Utf8Len(v, i);
        if (n > 1) { s_.append(v, i, n); i += n - 1; }
        else { char b[8]; std::snprintf(b, sizeof b, "\\u%04x", c); s_ += b; }
        continue;
      }
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
// frames
// ------------------------------------------------------------------------------------------------

struct Frame
{
  std::string json;
  std::string bin;
};

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

struct Refuse
{
  std::string code, detail;
};

static Frame Refusal(const Refuse& r)
{
  JsonOut j;
  j.Begin().Bool("ok", false).Str("code", r.code).Str("detail", r.detail).End();
  return {j.Text(), {}};
}

static bool ReadExact(FILE* f, char* p, size_t n)
{
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

enum class FrameRead { Ok, End, TooLarge };

static FrameRead ReadFrame(Frame& fr, uint32_t& jl, uint32_t& bl)
{
  unsigned char h[8];
  if (!ReadExact(stdin, reinterpret_cast<char*>(h), 8)) return FrameRead::End;
  jl = Le32(h);
  bl = Le32(h + 4);
  // A header that announces more than any request carries means the stream is out of step (a stray byte,
  // a byte-order mark: the G0 Windows session's first attempt read EF BB BF as the start of a header and
  // waited for 297,778,159 bytes). Waiting would be a hang; the caller answers and exits.
  if (jl > kMaxJson || bl > kMaxBin) return FrameRead::TooLarge;
  fr.json.assign(jl, '\0');
  fr.bin.assign(bl, '\0');
  if (jl > 0 && !ReadExact(stdin, &fr.json[0], jl)) return FrameRead::End;
  if (bl > 0 && !ReadExact(stdin, &fr.bin[0], bl)) return FrameRead::End;
  return FrameRead::Ok;
}

static void WriteFrame(const Frame& fr)
{
  std::string h;
  PutLe32(h, static_cast<uint32_t>(fr.json.size()));
  PutLe32(h, static_cast<uint32_t>(fr.bin.size()));
  std::fwrite(h.data(), 1, h.size(), g_proto);
  std::fwrite(fr.json.data(), 1, fr.json.size(), g_proto);
  if (!fr.bin.empty()) std::fwrite(fr.bin.data(), 1, fr.bin.size(), g_proto);
  std::fflush(g_proto);
}

static std::map<std::string, std::string> RequestBlobs(const Json& req, const std::string& bin)
{
  std::map<std::string, std::string> out;
  const Json* list = req.Get("blobs");
  if (list == nullptr) return out;
  if (list->kind != Json::Array) throw Refuse{"request.malformed", "\"blobs\" is not an array"};
  size_t at = 0;
  for (const Json& b : list->items)
  {
    const Json* n = b.Get("name");
    const Json* t = b.Get("type");
    const Json* c = b.Get("count");
    if (n == nullptr || t == nullptr || c == nullptr || n->kind != Json::String || t->kind != Json::String || c->kind != Json::Number)
      throw Refuse{"request.malformed", "a blob is declared without a name, a type and a count"};
    size_t unit = t->text == "f64" ? 8 : t->text == "u32" ? 4 : t->text == "bytes" ? 1 : 0;
    if (unit == 0) throw Refuse{"request.malformed", "blob type \"" + t->text + "\" is not f64, u32 or bytes"};
    size_t len = static_cast<size_t>(c->number) * unit;
    if (at + len > bin.size()) throw Refuse{"request.malformed", "the blobs declare more bytes than the frame carries"};
    out[n->text] = bin.substr(at, len);
    at += len;
  }
  if (at != bin.size()) throw Refuse{"request.malformed", "the frame carries bytes no blob declares"};
  return out;
}

static std::string Bytes(const void* p, size_t n) { return std::string(static_cast<const char*>(p), n); }

// ------------------------------------------------------------------------------------------------
// request members
// ------------------------------------------------------------------------------------------------

static const Json& Need(const Json& o, const char* key, Json::Kind kind)
{
  const Json* v = o.Get(key);
  if (v == nullptr || v->kind != kind) throw Refuse{"request.malformed", std::string("\"") + key + "\" is missing or of the wrong type"};
  return *v;
}

static double NumOr(const Json& o, const char* key, double fallback)
{
  const Json* v = o.Get(key);
  return v != nullptr && v->kind == Json::Number ? v->number : fallback;
}

static bool BoolOr(const Json& o, const char* key, bool fallback)
{
  const Json* v = o.Get(key);
  return v != nullptr && v->kind == Json::Bool ? v->boolean : fallback;
}

static std::string StrOr(const Json& o, const char* key, const std::string& fallback)
{
  const Json* v = o.Get(key);
  return v != nullptr && v->kind == Json::String ? v->text : fallback;
}

// ------------------------------------------------------------------------------------------------
// gdstk's error logger, captured per call
//
// gdstk reports through a global FILE* (error_logger) as well as through ErrorCode. Each read and write
// points it at a temporary file and hands back the lines, so a reply can say what gdstk said and the
// next call starts clean. Copied to stderr too, for a person watching.
// ------------------------------------------------------------------------------------------------

#if defined(_WIN32)
static std::wstring Utf16(const std::string& utf8)
{
  int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), (int)utf8.size(), nullptr, 0);
  if (n <= 0) return std::wstring();
  std::wstring wide((size_t)n, L'\0');
  MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), (int)utf8.size(), &wide[0], n);
  return wide;
}
#endif

// A path as this machine's C library can open it. On Windows a path past MAX_PATH opens only in its \\?\
// form unless the system's LongPathsEnabled policy is on, which it is not by default and which longPathAware
// in the manifest cannot turn on (the G0 and G1 Windows sessions: 365-372 characters refused plain, read and
// written prefixed). So the worker adds the prefix itself, and no client has to: the path is made absolute
// and normalised first (GetFullPathNameW -- \\?\ turns normalisation off), and a UNC path becomes
// \\?\UNC\server\share\.... Below MAX_PATH - 12 (the directory limit, which also leaves room for a
// write's ".part") a path is passed on untouched, as is one already in a \\?\ or \\.\ form.
static std::string NativePath(const std::string& utf8)
{
#if defined(_WIN32)
  if (utf8.rfind("\\\\?\\", 0) == 0 || utf8.rfind("\\\\.\\", 0) == 0) return utf8;
  std::wstring wide = Utf16(utf8);
  if (wide.empty()) return utf8;
  DWORD need = GetFullPathNameW(wide.c_str(), 0, nullptr, nullptr);
  if (need == 0) return utf8;
  std::wstring full((size_t)need, L'\0');
  DWORD got = GetFullPathNameW(wide.c_str(), need, &full[0], nullptr);
  if (got == 0 || got >= need) return utf8;
  full.resize(got);
  if (full.size() < MAX_PATH - 12) return utf8;
  std::wstring prefixed = full.rfind(L"\\\\", 0) == 0 ? L"\\\\?\\UNC\\" + full.substr(2) : L"\\\\?\\" + full;
  int m = WideCharToMultiByte(CP_UTF8, 0, prefixed.data(), (int)prefixed.size(), nullptr, 0, nullptr, nullptr);
  if (m <= 0) return utf8;
  std::string out((size_t)m, '\0');
  WideCharToMultiByte(CP_UTF8, 0, prefixed.data(), (int)prefixed.size(), &out[0], m, nullptr, nullptr);
  return out;
#else
  return utf8;
#endif
}

// A write's ".part" put in place of the target, and a ".part" removed. On Windows these go to the UTF-16 API:
// the C library's rename refused a \\?\ path with ENOENT (Wine, after NativePath above), where
// MoveFileExW takes it -- and replaces the target in one call, where rename would not.
static bool ReplaceWith(const std::string& tmp, const std::string& path)
{
#if defined(_WIN32)
  return MoveFileExW(Utf16(tmp).c_str(), Utf16(path).c_str(), MOVEFILE_REPLACE_EXISTING) != 0;
#else
  std::remove(path.c_str());
  return std::rename(tmp.c_str(), path.c_str()) == 0;
#endif
}

static void RemoveFile(const std::string& path)
{
#if defined(_WIN32)
  DeleteFileW(Utf16(path).c_str());
#else
  std::remove(path.c_str());
#endif
}

// tmpfile() on Windows creates its file in the ROOT of the current drive, which an ordinary user may not
// write: under Wine it failed, and every gdstk message was lost (Q3). Windows gets a file of its own in
// %TEMP%, opened "D" (deleted when closed) and "T" (kept in memory where it can be).
static FILE* CaptureFile()
{
#if defined(_WIN32)
  wchar_t dir[MAX_PATH + 1], name[MAX_PATH + 1];
  if (GetTempPathW(MAX_PATH, dir) == 0 || GetTempFileNameW(dir, L"gdk", 0, name) == 0) return nullptr;
  return _wfopen(name, L"w+bTD");
#else
  return std::tmpfile();
#endif
}

class LoggerCapture
{
public:
  LoggerCapture() : f_(CaptureFile()) { previous_ = error_logger; if (f_) error_logger = f_; }
  ~LoggerCapture() { error_logger = previous_; if (f_) std::fclose(f_); }

  std::vector<std::string> Lines()
  {
    std::vector<std::string> out;
    if (!f_) return out;
    std::fflush(f_);
    std::rewind(f_);
    char buf[2048];
    while (std::fgets(buf, sizeof buf, f_))
    {
      std::string s(buf);
      while (!s.empty() && (s.back() == '\n' || s.back() == '\r')) s.pop_back();
      if (!s.empty()) { out.push_back(s); std::fprintf(stderr, "%s\n", s.c_str()); }
    }
    return out;
  }

private:
  FILE* f_;
  FILE* previous_;
};

static const char* ErrorName(ErrorCode e)
{
  switch (e)
  {
    case ErrorCode::NoError: return "NoError";
    case ErrorCode::BooleanError: return "BooleanError";
    case ErrorCode::EmptyPath: return "EmptyPath";
    case ErrorCode::IntersectionNotFound: return "IntersectionNotFound";
    case ErrorCode::MissingReference: return "MissingReference";
    case ErrorCode::UnsupportedRecord: return "UnsupportedRecord";
    case ErrorCode::UnofficialSpecification: return "UnofficialSpecification";
    case ErrorCode::InvalidRepetition: return "InvalidRepetition";
    case ErrorCode::Overflow: return "Overflow";
    case ErrorCode::ChecksumError: return "ChecksumError";
    case ErrorCode::OutputFileOpenError: return "OutputFileOpenError";
    case ErrorCode::InputFileOpenError: return "InputFileOpenError";
    case ErrorCode::InputFileError: return "InputFileError";
    case ErrorCode::FileError: return "FileError";
    case ErrorCode::InvalidFile: return "InvalidFile";
    case ErrorCode::InsufficientMemory: return "InsufficientMemory";
    case ErrorCode::ZlibError: return "ZlibError";
  }
  return "Unknown";
}

// ------------------------------------------------------------------------------------------------
// held libraries
// ------------------------------------------------------------------------------------------------

struct Held
{
  Library lib = {};
  std::string format;
  double unit_m = 0;       // the file's user unit (GDSII) or 1e-6 (OASIS)
  double precision_m = 0;  // the file's database unit
  std::map<std::string, Cell*> byName;
};

struct Writing
{
  Library lib = {};
  std::string format;
  double precision_m = 0;
  double grid_per_um = 0;  // database units per USER unit (unit / precision): what every coordinate is divided by
  int deflate = 6;
  uint16_t flags = 0;
  uint64_t max_points = 0;
};

static std::map<int, std::unique_ptr<Held>> g_held;
static std::map<int, std::unique_ptr<Writing>> g_writing;
static int g_next = 1;

static void FreeLibrary(Library& lib)
{
  lib.free_all();
}

// ------------------------------------------------------------------------------------------------
// reading
// ------------------------------------------------------------------------------------------------

// An OASIS file ENDS with its END record, exactly 256 bytes long (SEMI P39 §14): record id 2, the
// table-offsets when START's offset-flag says they live here, a padding b-string, the validation scheme
// (0 none, 1 CRC32, 2 checksum32) and, for 1 and 2, four signature bytes. gdstk's read_oas faults on a
// truncated file (Q3: SIGSEGV on 23 of 100 truncations) and oas_validate reads only the last five bytes,
// so a truncated file can pass it. This walks the last 256 bytes as an END record and refuses a file
// whose bytes are not one.
static bool ReadVarint(const unsigned char*& p, const unsigned char* end, uint64_t& v)
{
  v = 0;
  for (int shift = 0; p < end && shift < 64; shift += 7)
  {
    unsigned char c = *p++;
    v |= (uint64_t)(c & 0x7F) << shift;
    if (!(c & 0x80)) return true;
  }
  return false;
}

static bool SkipReal(const unsigned char*& p, const unsigned char* end)
{
  uint64_t t, a;
  if (!ReadVarint(p, end, t)) return false;
  switch (t)
  {
    case 0: case 1: case 2: case 3: return ReadVarint(p, end, a);
    case 4: case 5: return ReadVarint(p, end, a) && ReadVarint(p, end, a);
    case 6: if (end - p < 4) return false; p += 4; return true;
    case 7: if (end - p < 8) return false; p += 8; return true;
  }
  return false;
}

static std::string EndRecordProblem(const std::string& path)
{
  FILE* f = std::fopen(path.c_str(), "rb");
  if (!f) return "the file could not be opened";
  // 64-bit offsets: `long` is 32 bits on Windows, and an OASIS file can be larger than 2 GB.
  crf_fseek64(f, 0, SEEK_END);
  long long size = (long long)crf_ftell64(f);
  unsigned char head[64] = {}, tail[256] = {};
  crf_fseek64(f, 0, SEEK_SET);
  size_t hn = std::fread(head, 1, sizeof head, f);
  bool ok = size >= 14 + 256;
  if (ok)
  {
    crf_fseek64(f, size - 256, SEEK_SET);
    ok = std::fread(tail, 1, 256, f) == 256;
  }
  std::fclose(f);
  if (!ok) return "the file is shorter than an OASIS header and END record";
  // START: magic, record 1, "1.0" (a-string), unit (real), offset-flag.
  const unsigned char* p = head + 14;
  const unsigned char* he = head + hn;
  uint64_t n, flag;
  if (!ReadVarint(p, he, n) || (uint64_t)(he - p) < n) return "the START record cannot be read";
  p += n;
  if (!SkipReal(p, he) || !ReadVarint(p, he, flag)) return "the START record cannot be read";
  // END
  const unsigned char* q = tail;
  const unsigned char* te = tail + 256;
  if (*q++ != 2) return "the last 256 bytes are not an END record (the file may be truncated)";
  if (flag == 1)
    for (int i = 0; i < 12; i++)
      if (!ReadVarint(q, te, n)) return "the END record's table-offsets cannot be read";
  if (!ReadVarint(q, te, n) || (uint64_t)(te - q) < n) return "the END record's padding cannot be read";
  q += n;
  uint64_t scheme;
  if (!ReadVarint(q, te, scheme) || scheme > 2) return "the END record's validation scheme cannot be read";
  if (scheme != 0) q += 4;
  if (q != te) return "the END record is not 256 bytes long (the file may be truncated)";
  return "";
}

// OASIS: read_oas STOPS at the first record it reports as UnsupportedRecord (XNAME, XELEMENT,
// XGEOMETRY, an unknown record) whenever an error-code pointer is passed, because its loop runs only
// while that code is NoError -- and UnsupportedRecord is classed as a WARNING. Passing no pointer
// reads to the END record. So the worker passes none, captures the logger, and treats a library with
// no name as a read that never reached END (read_oas names the library "LIB" only at END).
static Frame Open(const Json& req)
{
  std::string path = NativePath(Need(req, "path", Json::String).text);
  std::string format = Need(req, "format", Json::String).text;
  if (format != "gds" && format != "oas") throw Refuse{"request.malformed", "\"format\" is not gds or oas"};

  // A file that cannot be opened is said so, before any reader can misreport it as damaged (the G0
  // Windows session: a 365-character path without \\?\ was refused as "truncated").
  if (FILE* probe = std::fopen(path.c_str(), "rb")) std::fclose(probe);
  else throw Refuse{"read.open-failed", std::string("the file could not be opened: ") + std::strerror(errno)};

  auto h = std::make_unique<Held>();
  h->format = format;
  LoggerCapture log;
  ErrorCode err = ErrorCode::NoError;
  double tolerance = NumOr(req, "tolerance_dbu", 0.01);

  if (format == "gds")
  {
    double unit = 0, precision = 0;
    err = gds_units(path.c_str(), unit, precision);
    if (err != ErrorCode::NoError || precision <= 0)
    {
      auto lines = log.Lines();
      throw Refuse{"read.failed", std::string("gds_units: ") + ErrorName(err) + (lines.empty() ? "" : " -- " + lines.back())};
    }
    h->unit_m = unit;
    h->precision_m = precision;
    // unit = the file's own database unit: gdstk's factor is precision / precision = 1.0, exactly.
    h->lib = read_gds(path.c_str(), precision, tolerance, nullptr, &err);
    if (err != ErrorCode::NoError && err != ErrorCode::MissingReference && err != ErrorCode::UnsupportedRecord)
    {
      auto lines = log.Lines();
      FreeLibrary(h->lib);
      throw Refuse{"read.failed", std::string("read_gds: ") + ErrorName(err) + (lines.empty() ? "" : " -- " + lines.back())};
    }
    if (h->lib.name == nullptr)
    {
      auto lines = log.Lines();
      FreeLibrary(h->lib);
      throw Refuse{"read.failed", std::string("read_gds returned no library (") + ErrorName(err) + ")" + (lines.empty() ? "" : " -- " + lines.back())};
    }
  }
  else
  {
    // read_oas never checks the CRC32 / checksum32 a file's END carries; oas_validate does (and answers
    // true for a file that carries none). Both guards are always on; "validate": false skips them only
    // under the test switch, so the spike's Q3 can count what gdstk does unguarded.
    if (BoolOr(req, "validate", true) || !TestSwitch())
    {
      std::string endProblem = EndRecordProblem(path);
      if (!endProblem.empty()) throw Refuse{"read.truncated", endProblem};
      uint32_t sig = 0;
      ErrorCode verr = ErrorCode::NoError;
      if (!oas_validate(path.c_str(), &sig, &verr))
      {
        auto lines = log.Lines();
        throw Refuse{"read.checksum", std::string("the file's validation signature does not match its bytes (") + ErrorName(verr) + ")" +
                                      (lines.empty() ? "" : " -- " + lines.back())};
      }
    }
    // (the guards come first: oas_precision faults on a file shorter than its START record)
    double precision = 0;
    err = oas_precision(path.c_str(), precision);
    if (err != ErrorCode::NoError || !(precision > 0))
    {
      auto lines = log.Lines();
      throw Refuse{"read.failed", std::string("oas_precision: ") + ErrorName(err) + (lines.empty() ? "" : " -- " + lines.back())};
    }
    h->unit_m = 1e-6;
    h->precision_m = precision;
    h->lib = read_oas(path.c_str(), precision, tolerance, nullptr);
    if (h->lib.name == nullptr)
    {
      auto lines = log.Lines();
      FreeLibrary(h->lib);
      throw Refuse{"read.failed", std::string("read_oas did not reach the END record") + (lines.empty() ? "" : " -- " + lines.back())};
    }
  }

  std::vector<std::string> messages = log.Lines();

  // gdstk reports a CBLOCK it cannot inflate and then parses the buffer anyway, which holds bytes it
  // never wrote: the result differs between platforms, and between two paths to the same file (findings
  // Q3). Nothing read after that message can be trusted, so the read is refused.
  for (const auto& m : messages)
    if (m.find("Unable to decompress CBLOCK") != std::string::npos)
    {
      FreeLibrary(h->lib);
      throw Refuse{"read.corrupt", "a compressed block (CBLOCK) could not be decompressed -- " + m};
    }

  // A cell with no name (a CELL record whose CELLNAME never arrived) or two cells of one name are
  // refusals: everything downstream keys on the name.
  for (uint64_t i = 0; i < h->lib.cell_array.count; i++)
  {
    Cell* c = h->lib.cell_array[i];
    if (c->name == nullptr)
    {
      FreeLibrary(h->lib);
      throw Refuse{"read.failed", "a cell has no name"};
    }
    if (h->byName.count(c->name))
    {
      FreeLibrary(h->lib);
      throw Refuse{"read.failed", std::string("two cells are named \"") + c->name + "\""};
    }
    h->byName[c->name] = c;
  }

  Array<Cell*> tops = {};
  Array<RawCell*> rawTops = {};
  h->lib.top_level(tops, rawTops);
  std::map<std::string, bool> isTop;
  for (uint64_t i = 0; i < tops.count; i++) isTop[tops[i]->name] = true;
  tops.clear();
  rawTops.clear();

  Reply r;
  int handle = g_next++;
  r.j.Int("handle", handle).Num("unit_m", h->unit_m).Num("precision_m", h->precision_m);
  r.j.Key("cells").BeginArr();
  for (uint64_t i = 0; i < h->lib.cell_array.count; i++)
  {
    Cell* c = h->lib.cell_array[i];
    r.j.Begin().Str("name", c->name)
        .Int("polygons", (long long)c->polygon_array.count)
        .Int("paths", (long long)(c->flexpath_array.count + c->robustpath_array.count))
        .Int("labels", (long long)c->label_array.count)
        .Int("refs", (long long)c->reference_array.count)
        .Bool("top", isTop.count(c->name) > 0)
        .End();
  }
  r.j.EndArr();
  r.j.Key("layer_names").BeginArr();
  for (uint64_t i = 0; i < h->lib.layer_names.count; i++)
  {
    const LayerName& ln = h->lib.layer_names[i];
    r.j.Begin().Str("name", ln.name ? ln.name : "").Str("kind", ln.type == LayerNameType::TEXT ? "text" : "geometry")
        .Int("layer_type", (int)ln.layer_interval.type).Int("layer_a", (long long)ln.layer_interval.bound_a).Int("layer_b", (long long)ln.layer_interval.bound_b)
        .Int("type_type", (int)ln.type_interval.type).Int("type_a", (long long)ln.type_interval.bound_a).Int("type_b", (long long)ln.type_interval.bound_b)
        .End();
  }
  r.j.EndArr();
  r.j.Key("messages").BeginArr();
  for (auto& m : messages) r.j.Str(m);
  r.j.EndArr();
  r.j.Str("error_code", ErrorName(err));
  r.j.Int("library_properties", h->lib.properties != nullptr ? 1 : 0);
  g_held[handle] = std::move(h);
  return r.Finish();
}

// Every coordinate leaves as the integer it is meant to be (see COORDINATES at the top). A value off its
// integer by more than a few ulps was not meant to be one (a CIRCLE's vertices, gdstk's own polygon) and
// is counted, so the client can say how many points it rounded.
static long long g_offGrid = 0;

static double Grid(double v)
{
  // std::round, not llround: the same answer for every value a coordinate can be, and defined for a value
  // no 64-bit integer holds (gdstk#247 writes ~2^64 for a negative explicit offset; findings Q5).
  double r = std::round(v);
  // Coordinates are bounded by 2^31, where an ulp is ~5e-7: a tenth of a thousandth of a unit is far above
  // the read factor's noise and far below a point that is genuinely off the grid.
  if (std::fabs(v - r) > 1e-4) g_offGrid++;
  return r;
}

static void WriteRepetition(JsonOut& j, const Repetition& rep)
{
  switch (rep.type)
  {
    case RepetitionType::None: return;
    case RepetitionType::Rectangular:
      j.Key("rep").Begin().Str("kind", "rectangular").Int("columns", (long long)rep.columns).Int("rows", (long long)rep.rows);
      j.Key("spacing").BeginArr().Num(Grid(rep.spacing.x)).Num(Grid(rep.spacing.y)).EndArr().End();
      return;
    case RepetitionType::Regular:
      j.Key("rep").Begin().Str("kind", "regular").Int("columns", (long long)rep.columns).Int("rows", (long long)rep.rows);
      j.Key("v1").BeginArr().Num(Grid(rep.v1.x)).Num(Grid(rep.v1.y)).EndArr();
      j.Key("v2").BeginArr().Num(Grid(rep.v2.x)).Num(Grid(rep.v2.y)).EndArr().End();
      return;
    case RepetitionType::Explicit:
      j.Key("rep").Begin().Str("kind", "explicit").Key("offsets").BeginArr();
      for (uint64_t i = 0; i < rep.offsets.count; i++) j.Num(Grid(rep.offsets[i].x)).Num(Grid(rep.offsets[i].y));
      j.EndArr().End();
      return;
    case RepetitionType::ExplicitX:
    case RepetitionType::ExplicitY:
      j.Key("rep").Begin().Str("kind", rep.type == RepetitionType::ExplicitX ? "explicit_x" : "explicit_y").Key("coords").BeginArr();
      for (uint64_t i = 0; i < rep.coords.count; i++) j.Num(Grid(rep.coords[i]));
      j.EndArr().End();
      return;
  }
}

static const char* EndName(EndType e)
{
  switch (e)
  {
    case EndType::Flush: return "flush";
    case EndType::Round: return "round";
    case EndType::HalfWidth: return "halfwidth";
    case EndType::Extended: return "extended";
    case EndType::Smooth: return "smooth";
    case EndType::Function: return "function";
  }
  return "unknown";
}

static Frame CellReply(const Json& req)
{
  int handle = (int)Need(req, "handle", Json::Number).number;
  auto it = g_held.find(handle);
  if (it == g_held.end()) throw Refuse{"handle.unknown", "no open library has handle " + std::to_string(handle)};
  std::string name = Need(req, "name", Json::String).text;
  auto c = it->second->byName.find(name);
  if (c == it->second->byName.end()) throw Refuse{"cell.unknown", "the library has no cell \"" + name + "\""};
  Cell* cell = c->second;

  Reply r;
  std::vector<double> xy, pathXy;
  long long notes_properties = 0, notes_robust = 0, notes_multi = 0;
  g_offGrid = 0;

  r.j.Str("name", cell->name);
  r.j.Key("polygons").BeginArr();
  for (uint64_t i = 0; i < cell->polygon_array.count; i++)
  {
    const Polygon* p = cell->polygon_array[i];
    r.j.Begin().Int("layer", get_layer(p->tag)).Int("datatype", get_type(p->tag)).Int("n", (long long)p->point_array.count);
    WriteRepetition(r.j, p->repetition);
    r.j.End();
    for (uint64_t k = 0; k < p->point_array.count; k++) { xy.push_back(Grid(p->point_array[k].x)); xy.push_back(Grid(p->point_array[k].y)); }
    if (p->properties) notes_properties++;
  }
  r.j.EndArr();

  r.j.Key("paths").BeginArr();
  for (uint64_t i = 0; i < cell->flexpath_array.count; i++)
  {
    const FlexPath* fp = cell->flexpath_array[i];
    if (fp->num_elements != 1) notes_multi++;
    for (uint64_t e = 0; e < fp->num_elements; e++)
    {
      const FlexPathElement& el = fp->elements[e];
      double hw = el.half_width_and_offset.count > 0 ? el.half_width_and_offset[0].u : 0;
      r.j.Begin().Int("layer", get_layer(el.tag)).Int("datatype", get_type(el.tag)).Num("width", Grid(2 * hw))
          .Str("end", EndName(el.end_type)).Bool("simple", fp->simple_path).Bool("scale_width", fp->scale_width)
          .Int("n", (long long)fp->spine.point_array.count);
      if (el.end_type == EndType::Extended) r.j.Key("ext").BeginArr().Num(Grid(el.end_extensions.u)).Num(Grid(el.end_extensions.v)).EndArr();
      WriteRepetition(r.j, fp->repetition);
      r.j.End();
      for (uint64_t k = 0; k < fp->spine.point_array.count; k++) { pathXy.push_back(Grid(fp->spine.point_array[k].x)); pathXy.push_back(Grid(fp->spine.point_array[k].y)); }
    }
    if (fp->properties) notes_properties++;
  }
  r.j.EndArr();
  notes_robust = (long long)cell->robustpath_array.count;

  r.j.Key("labels").BeginArr();
  for (uint64_t i = 0; i < cell->label_array.count; i++)
  {
    const Label* l = cell->label_array[i];
    r.j.Begin().Int("layer", get_layer(l->tag)).Int("texttype", get_type(l->tag)).Str("text", l->text ? l->text : "")
        .Num("x", Grid(l->origin.x)).Num("y", Grid(l->origin.y)).Int("anchor", (int)l->anchor)
        .Num("rotation", l->rotation * 180.0 / kPi).Num("magnification", l->magnification).Bool("mirror", l->x_reflection);
    WriteRepetition(r.j, l->repetition);
    r.j.End();
    if (l->properties) notes_properties++;
  }
  r.j.EndArr();

  r.j.Key("refs").BeginArr();
  for (uint64_t i = 0; i < cell->reference_array.count; i++)
  {
    const Reference* ref = cell->reference_array[i];
    const char* target = ref->type == ReferenceType::Cell ? ref->cell->name
                       : ref->type == ReferenceType::RawCell ? ref->rawcell->name
                       : ref->name;
    r.j.Begin().Str("cell", target ? target : "").Bool("resolved", ref->type == ReferenceType::Cell)
        .Num("x", Grid(ref->origin.x)).Num("y", Grid(ref->origin.y))
        .Num("rotation", ref->rotation * 180.0 / kPi).Num("magnification", ref->magnification).Bool("mirror", ref->x_reflection);
    WriteRepetition(r.j, ref->repetition);
    r.j.End();
    if (ref->properties) notes_properties++;
  }
  r.j.EndArr();

  r.j.Key("notes").Begin().Int("elements_with_properties", notes_properties).Int("robust_paths", notes_robust)
      .Int("multi_element_paths", notes_multi).Bool("cell_properties", cell->properties != nullptr)
      .Int("off_grid_rounded", g_offGrid).End();

  r.Blob("xy", "f64", xy.size(), Bytes(xy.data(), xy.size() * sizeof(double)));
  r.Blob("path_xy", "f64", pathXy.size(), Bytes(pathXy.data(), pathXy.size() * sizeof(double)));
  return r.Finish();
}

static Frame Close(const Json& req)
{
  int handle = (int)Need(req, "handle", Json::Number).number;
  auto it = g_held.find(handle);
  if (it == g_held.end()) throw Refuse{"handle.unknown", "no open library has handle " + std::to_string(handle)};
  FreeLibrary(it->second->lib);
  g_held.erase(it);
  Reply r;
  return r.Finish();
}

// ------------------------------------------------------------------------------------------------
// writing
// ------------------------------------------------------------------------------------------------

static Frame BeginWrite(const Json& req)
{
  auto w = std::make_unique<Writing>();
  w->format = Need(req, "format", Json::String).text;
  if (w->format != "gds" && w->format != "oas") throw Refuse{"request.malformed", "\"format\" is not gds or oas"};
  // §5: the library's own unit_m and precision_m, as circuitRF's export states them (GdsiiExport writes
  // 1e-6 and 1e-6 / DbuPerMicron). Coordinates arrive in database units and are handed to gdstk in user
  // units, n / (unit / precision); gdstk multiplies back by unit / precision and rounds (Q6: exact).
  double unit = NumOr(req, "unit_m", 1e-6);
  double precision = NumOr(req, "precision_m", 0);
  if (precision <= 0)
  {
    double grid = NumOr(req, "grid_per_um", 0);
    if (!(grid > 0)) throw Refuse{"request.malformed", "a write states precision_m (or grid_per_um)"};
    precision = 1e-6 / grid;
  }
  if (!(unit > 0) || !(precision > 0)) throw Refuse{"request.malformed", "unit_m and precision_m must be positive"};
  w->precision_m = precision;
  w->grid_per_um = unit / precision;  // database units per user unit: the divisor
  w->lib.init("LIB", unit, precision);
  const Json* opt = req.Get("options");
  if (opt != nullptr && opt->kind == Json::Object)
  {
    w->deflate = (int)NumOr(*opt, "compression_level", 6);
    if (BoolOr(*opt, "detect_rectangles", true)) w->flags |= OASIS_CONFIG_DETECT_RECTANGLES;
    if (BoolOr(*opt, "detect_trapezoids", true)) w->flags |= OASIS_CONFIG_DETECT_TRAPEZOIDS;
    std::string v = StrOr(*opt, "validation", "crc32");
    if (v == "crc32") w->flags |= OASIS_CONFIG_INCLUDE_CRC32;
    else if (v == "checksum32") w->flags |= OASIS_CONFIG_INCLUDE_CHECKSUM32;
    if (BoolOr(*opt, "standard_properties", false)) w->flags |= OASIS_CONFIG_STANDARD_PROPERTIES;
    w->max_points = (uint64_t)NumOr(*opt, "max_points", 0);
  }
  else
  {
    w->flags = OASIS_CONFIG_DETECT_RECTANGLES | OASIS_CONFIG_DETECT_TRAPEZOIDS | OASIS_CONFIG_INCLUDE_CRC32;
  }
  int handle = g_next++;
  g_writing[handle] = std::move(w);
  Reply r;
  r.j.Int("handle", handle);
  return r.Finish();
}

static void ReadRepetition(const Json& o, double s, Repetition& rep)
{
  const Json* r = o.Get("rep");
  if (r == nullptr || r->kind != Json::Object) return;
  std::string kind = Need(*r, "kind", Json::String).text;
  if (kind == "rectangular")
  {
    rep.type = RepetitionType::Rectangular;
    rep.columns = (uint64_t)Need(*r, "columns", Json::Number).number;
    rep.rows = (uint64_t)Need(*r, "rows", Json::Number).number;
    const Json& sp = Need(*r, "spacing", Json::Array);
    rep.spacing = Vec2{sp.items.at(0).number / s, sp.items.at(1).number / s};
  }
  else if (kind == "regular")
  {
    rep.type = RepetitionType::Regular;
    rep.columns = (uint64_t)Need(*r, "columns", Json::Number).number;
    rep.rows = (uint64_t)Need(*r, "rows", Json::Number).number;
    const Json& v1 = Need(*r, "v1", Json::Array);
    const Json& v2 = Need(*r, "v2", Json::Array);
    rep.v1 = Vec2{v1.items.at(0).number / s, v1.items.at(1).number / s};
    rep.v2 = Vec2{v2.items.at(0).number / s, v2.items.at(1).number / s};
  }
  else if (kind == "explicit")
  {
    rep.type = RepetitionType::Explicit;
    rep.offsets = {};
    const Json& off = Need(*r, "offsets", Json::Array);
    for (size_t i = 0; i + 1 < off.items.size(); i += 2) rep.offsets.append(Vec2{off.items[i].number / s, off.items[i + 1].number / s});
  }
  else if (kind == "explicit_x" || kind == "explicit_y")
  {
    rep.type = kind == "explicit_x" ? RepetitionType::ExplicitX : RepetitionType::ExplicitY;
    rep.coords = {};
    for (const Json& c : Need(*r, "coords", Json::Array).items) rep.coords.append(c.number / s);
  }
  else throw Refuse{"request.malformed", "unknown repetition kind \"" + kind + "\""};
}

static Frame AddCell(const Json& req, const std::string& bin)
{
  int handle = (int)Need(req, "handle", Json::Number).number;
  auto it = g_writing.find(handle);
  if (it == g_writing.end()) throw Refuse{"handle.unknown", "no write has handle " + std::to_string(handle)};
  Writing& w = *it->second;
  const double s = w.grid_per_um;  // database units -> micrometres

  auto blobs = RequestBlobs(req, bin);
  const std::string& xyb = blobs["xy"];
  const std::string& pxyb = blobs["path_xy"];
  const double* xy = reinterpret_cast<const double*>(xyb.data());
  const double* pxy = reinterpret_cast<const double*>(pxyb.data());
  size_t xyn = xyb.size() / 8, pxyn = pxyb.size() / 8, xi = 0, pi = 0;

  Cell* cell = (Cell*)allocate_clear(sizeof(Cell));
  cell->name = copy_string(Need(req, "name", Json::String).text.c_str(), nullptr);

  if (const Json* polys = req.Get("polygons"))
    for (const Json& p : polys->items)
    {
      size_t n = (size_t)Need(p, "n", Json::Number).number;
      if (xi + 2 * n > xyn) throw Refuse{"request.malformed", "the polygons declare more vertices than \"xy\" carries"};
      Polygon* poly = (Polygon*)allocate_clear(sizeof(Polygon));
      poly->tag = make_tag((uint32_t)Need(p, "layer", Json::Number).number, (uint32_t)Need(p, "datatype", Json::Number).number);
      for (size_t k = 0; k < n; k++, xi += 2) poly->point_array.append(Vec2{xy[xi] / s, xy[xi + 1] / s});
      ReadRepetition(p, s, poly->repetition);
      cell->polygon_array.append(poly);
    }

  if (const Json* paths = req.Get("paths"))
    for (const Json& p : paths->items)
    {
      size_t n = (size_t)Need(p, "n", Json::Number).number;
      if (pi + 2 * n > pxyn || n < 1) throw Refuse{"request.malformed", "the paths declare more vertices than \"path_xy\" carries"};
      FlexPath* fp = (FlexPath*)allocate_clear(sizeof(FlexPath));
      FlexPathElement* el = (FlexPathElement*)allocate_clear(sizeof(FlexPathElement));
      fp->elements = el;
      fp->num_elements = 1;
      fp->simple_path = true;
      fp->scale_width = true;
      // A spine point closer than this to the previous one is removed by gdstk's own
      // remove_overlapping_points (strict <). A tenth of a database unit keeps a one-unit segment.
      fp->spine.tolerance = 0.1 / s;
      el->tag = make_tag((uint32_t)Need(p, "layer", Json::Number).number, (uint32_t)Need(p, "datatype", Json::Number).number);
      double half = Need(p, "width", Json::Number).number / 2 / s;
      std::string end = Need(p, "end", Json::String).text;
      if (end == "flush") el->end_type = EndType::Flush;
      else if (end == "round") el->end_type = EndType::Round;
      else if (end == "halfwidth") el->end_type = EndType::HalfWidth;
      else if (end == "extended")
      {
        el->end_type = EndType::Extended;
        const Json& ext = Need(p, "ext", Json::Array);
        el->end_extensions = Vec2{ext.items.at(0).number / s, ext.items.at(1).number / s};
      }
      else throw Refuse{"request.malformed", "unknown path end \"" + end + "\""};
      for (size_t k = 0; k < n; k++, pi += 2)
      {
        fp->spine.point_array.append(Vec2{pxy[pi] / s, pxy[pi + 1] / s});
        el->half_width_and_offset.append(Vec2{half, 0});
      }
      ReadRepetition(p, s, fp->repetition);
      cell->flexpath_array.append(fp);
    }

  if (const Json* labels = req.Get("labels"))
    for (const Json& l : labels->items)
    {
      Label* label = (Label*)allocate_clear(sizeof(Label));
      label->init(Need(l, "text", Json::String).text.c_str());
      label->tag = make_tag((uint32_t)Need(l, "layer", Json::Number).number, (uint32_t)NumOr(l, "texttype", 0));
      label->origin = Vec2{Need(l, "x", Json::Number).number / s, Need(l, "y", Json::Number).number / s};
      label->anchor = (Anchor)(int)NumOr(l, "anchor", (int)Anchor::O);
      label->rotation = NumOr(l, "rotation", 0) * kPi / 180.0;
      label->magnification = NumOr(l, "magnification", 1);
      label->x_reflection = BoolOr(l, "mirror", false);
      ReadRepetition(l, s, label->repetition);
      cell->label_array.append(label);
    }

  if (const Json* refs = req.Get("refs"))
    for (const Json& rj : refs->items)
    {
      Reference* ref = (Reference*)allocate_clear(sizeof(Reference));
      ref->type = ReferenceType::Name;
      ref->name = copy_string(Need(rj, "cell", Json::String).text.c_str(), nullptr);
      ref->origin = Vec2{Need(rj, "x", Json::Number).number / s, Need(rj, "y", Json::Number).number / s};
      ref->rotation = NumOr(rj, "rotation", 0) * kPi / 180.0;
      ref->magnification = NumOr(rj, "magnification", 1);
      ref->x_reflection = BoolOr(rj, "mirror", false);
      ReadRepetition(rj, s, ref->repetition);
      cell->reference_array.append(ref);
    }

  w.lib.cell_array.append(cell);
  Reply r;
  return r.Finish();
}

// References are added by NAME (the cells may arrive in any order); before writing they are resolved to
// the cells themselves, because gdstk's OASIS writer numbers references through the cell pointers.
static int ResolveReferences(Library& lib)
{
  std::map<std::string, Cell*> byName;
  for (uint64_t i = 0; i < lib.cell_array.count; i++) byName[lib.cell_array[i]->name] = lib.cell_array[i];
  int missing = 0;
  for (uint64_t i = 0; i < lib.cell_array.count; i++)
  {
    Cell* c = lib.cell_array[i];
    for (uint64_t k = 0; k < c->reference_array.count; k++)
    {
      Reference* ref = c->reference_array[k];
      if (ref->type != ReferenceType::Name) continue;
      auto it = byName.find(ref->name);
      if (it == byName.end()) { missing++; continue; }
      free_allocation(ref->name);
      ref->type = ReferenceType::Cell;
      ref->cell = it->second;
    }
  }
  return missing;
}

static Frame FinishWrite(const Json& req)
{
  int handle = (int)Need(req, "handle", Json::Number).number;
  auto it = g_writing.find(handle);
  if (it == g_writing.end()) throw Refuse{"handle.unknown", "no write has handle " + std::to_string(handle)};
  std::unique_ptr<Writing> w = std::move(it->second);
  g_writing.erase(it);
  std::string path = NativePath(Need(req, "path", Json::String).text);
  std::string tmp = path + ".part";

  int missing = ResolveReferences(w->lib);
  if (missing > 0)
  {
    FreeLibrary(w->lib);
    throw Refuse{"write.missing-cell", std::to_string(missing) + " reference(s) name a cell this write does not hold"};
  }

  LoggerCapture log;
  ErrorCode err;
  if (w->format == "gds")
  {
    // A fixed timestamp, so the same library is the same bytes (Q8's question, asked of GDSII too).
    tm stamp = {};
    stamp.tm_year = 126;  // 2026
    stamp.tm_mon = 0;
    stamp.tm_mday = 1;
    if (BoolOr(req, "now", false)) err = w->lib.write_gds(tmp.c_str(), w->max_points, nullptr);
    else err = w->lib.write_gds(tmp.c_str(), w->max_points, &stamp);
  }
  else
  {
    err = w->lib.write_oas(tmp.c_str(), 0, (uint8_t)w->deflate, w->flags);
  }
  auto lines = log.Lines();
  uint64_t cells = w->lib.cell_array.count;
  FreeLibrary(w->lib);
  if (err != ErrorCode::NoError && err != ErrorCode::EmptyPath)
  {
    RemoveFile(tmp);
    throw Refuse{"write.failed", std::string(ErrorName(err)) + (lines.empty() ? "" : " -- " + lines.back())};
  }
  if (!ReplaceWith(tmp, path))
  {
#if defined(_WIN32)
    std::string why = "Windows error " + std::to_string((unsigned long)GetLastError());
#else
    std::string why = std::strerror(errno);
#endif
    RemoveFile(tmp);
    throw Refuse{"write.failed", "could not rename the temporary file: " + why};
  }
  FILE* f = std::fopen(path.c_str(), "rb");
  long long bytes = -1;
  if (f) { crf_fseek64(f, 0, SEEK_END); bytes = (long long)crf_ftell64(f); std::fclose(f); }

  Reply r;
  r.j.Int("bytes", bytes).Int("cells", (long long)cells).Str("error_code", ErrorName(err));
  r.j.Key("messages").BeginArr();
  for (auto& m : lines) r.j.Str(m);
  r.j.EndArr();
  return r.Finish();
}

// ------------------------------------------------------------------------------------------------
// hello, version, selftest
// ------------------------------------------------------------------------------------------------

static std::string QhullVersion()
{
  // qh_version is "2020.2.r 2020/08/31"; the release is the first word.
  std::string v = qh_version;
  size_t sp = v.find(' ');
  return sp == std::string::npos ? v : v.substr(0, sp);
}

static Frame Hello(const Json&)
{
  Reply r;
  r.j.Str("worker", CRF_WORKER_VERSION).Str("gdstk", GDSTK_VERSION).Str("qhull", QhullVersion())
      .Str("zlib", zlibVersion()).Int("protocol", kProtocol).Str("rid", CompiledRid());
#if defined(_WIN32)
  // 65001 when the manifest's activeCodePage took effect: fopen then reads the UTF-8 paths the protocol
  // carries, on a machine of any system code page (findings Q3).
  r.j.Int("code_page", (long long)GetACP());
#endif
  return r.Finish();
}

static int PrintVersion()
{
  std::printf("gdstk-worker %s\ngdstk %s\nqhull %s\nzlib %s\nprotocol %d\nrid %s\n", CRF_WORKER_VERSION, GDSTK_VERSION,
              QhullVersion().c_str(), zlibVersion(), kProtocol, CompiledRid());
  return 0;
}

// One rectangle, one 5-vertex polygon, one path, one label and one 3 x 2 array, written to both
// formats in the temporary directory and read back; the counts must survive.
static Frame Selftest(const Json&)
{
  Library lib = {};
  lib.init("LIB", 1e-6, 1e-9);
  Cell* child = (Cell*)allocate_clear(sizeof(Cell));
  child->name = copy_string("child", nullptr);
  Polygon* rect = (Polygon*)allocate_clear(sizeof(Polygon));
  *rect = rectangle(Vec2{0, 0}, Vec2{1, 2}, make_tag(1, 0));
  child->polygon_array.append(rect);
  lib.cell_array.append(child);

  Cell* top = (Cell*)allocate_clear(sizeof(Cell));
  top->name = copy_string("top", nullptr);
  Polygon* poly = (Polygon*)allocate_clear(sizeof(Polygon));
  poly->tag = make_tag(2, 0);
  Vec2 pts[] = {{0, 0}, {3, 0}, {3, 1}, {1, 2}, {0, 1}};
  for (auto& p : pts) poly->point_array.append(p);
  top->polygon_array.append(poly);
  Label* label = (Label*)allocate_clear(sizeof(Label));
  label->init("hello");
  label->tag = make_tag(3, 0);
  label->origin = Vec2{0.5, 0.5};
  top->label_array.append(label);
  Reference* ref = (Reference*)allocate_clear(sizeof(Reference));
  ref->init(child);
  ref->origin = Vec2{10, 10};
  ref->repetition.type = RepetitionType::Rectangular;
  ref->repetition.columns = 3;
  ref->repetition.rows = 2;
  ref->repetition.spacing = Vec2{5, 5};
  top->reference_array.append(ref);
  lib.cell_array.append(top);

  std::string dir;
#if defined(_WIN32)
  const char* t = std::getenv("TEMP");
  dir = t ? t : ".";
  dir += "\\";
#else
  const char* t = std::getenv("TMPDIR");
  dir = t ? t : "/tmp";
  if (dir.back() != '/') dir += "/";
#endif
  // Named by process: two circuitRF processes may run their selftests at once.
  std::string stem = dir + "gdstk-worker-selftest-" + std::to_string((long long)crf_getpid());
  std::string gds = stem + ".gds", oas = stem + ".oas";

  LoggerCapture log;
  ErrorCode e1 = lib.write_gds(gds.c_str(), 0, nullptr);
  ErrorCode e2 = lib.write_oas(oas.c_str(), 0, 6, OASIS_CONFIG_DETECT_ALL | OASIS_CONFIG_INCLUDE_CRC32);
  lib.free_all();

  ErrorCode e3 = ErrorCode::NoError, e4 = ErrorCode::NoError;
  Library g = read_gds(gds.c_str(), 0, 0, nullptr, &e3);
  Library o = read_oas(oas.c_str(), 0, 0, &e4);
  bool valid = oas_validate(oas.c_str(), nullptr, nullptr);
  auto counts = [](Library& l) {
    long long n = 0;
    for (uint64_t i = 0; i < l.cell_array.count; i++)
      n += (long long)(l.cell_array[i]->polygon_array.count + l.cell_array[i]->label_array.count + l.cell_array[i]->reference_array.count);
    return n;
  };
  long long gc = counts(g), oc = counts(o);
  g.free_all();
  o.free_all();
  std::remove(gds.c_str());
  std::remove(oas.c_str());

  bool ok = e1 == ErrorCode::NoError && e2 == ErrorCode::NoError && e3 == ErrorCode::NoError && e4 == ErrorCode::NoError
         && gc == 4 && oc == 4 && valid;
  if (!ok)
    throw Refuse{"selftest.failed", std::string("gds ") + ErrorName(e1) + "/" + ErrorName(e3) + " " + std::to_string(gc) +
                                    " elements; oas " + ErrorName(e2) + "/" + ErrorName(e4) + " " + std::to_string(oc) +
                                    " elements, validate " + (valid ? "true" : "false")};
  Reply r;
  r.j.Int("gds_elements", gc).Int("oas_elements", oc).Bool("oas_valid", valid);
  return r.Finish();
}

// ------------------------------------------------------------------------------------------------
// dispatch
// ------------------------------------------------------------------------------------------------

static Frame Dispatch(const Frame& in, bool& quit)
{
  Json req;
  try { req = JsonReader(in.json).Document(); }
  catch (const std::exception& e) { throw Refuse{"request.malformed", e.what()}; }
  const Json* op = req.Get("op");
  if (op == nullptr || op->kind != Json::String) throw Refuse{"request.malformed", "a request names its \"op\""};
  const std::string& o = op->text;
  if (o == "hello") return Hello(req);
  if (o == "open") return Open(req);
  if (o == "cell") return CellReply(req);
  if (o == "close") return Close(req);
  if (o == "begin-write") return BeginWrite(req);
  if (o == "add-cell") return AddCell(req, in.bin);
  if (o == "finish-write") return FinishWrite(req);
  if (o == "selftest") return Selftest(req);
  if ((o == "crash" || o == "sleep") && !TestSwitch())
    throw Refuse{"request.unknown", "this worker has no op \"" + o + "\""};
  if (o == "crash") { std::fflush(g_proto); std::_Exit(70); }
  if (o == "sleep")
  {
    double seconds = NumOr(req, "seconds", 1);
#if defined(_WIN32)
    Sleep((DWORD)(seconds * 1000));
#else
    usleep((useconds_t)(seconds * 1e6));
#endif
    Reply r;
    return r.Finish();
  }
  if (o == "shutdown" || o == "quit") { quit = true; Reply r; return r.Finish(); }
  throw Refuse{"request.unknown", "this worker has no op \"" + o + "\""};
}

int main(int argc, char** argv)
{
  if (argc == 2 && (std::strcmp(argv[1], "--version") == 0 || std::strcmp(argv[1], "-v") == 0)) return PrintVersion();
  if (argc != 1)
  {
    std::fprintf(stderr,
                 "usage: gdstk-worker            speak the protocol on stdin/stdout (framed)\n"
                 "       gdstk-worker --version  print the worker, gdstk, qhull and zlib versions\n");
    return 2;
  }

  std::fflush(stdout);
  int protoFd = crf_dup(crf_fileno(stdout));
  if (protoFd >= 0 && crf_dup2(crf_fileno(stderr), crf_fileno(stdout)) >= 0)
    if (FILE* f = crf_fdopen(protoFd, "wb")) g_proto = f;
#if defined(_WIN32)
  _setmode(crf_fileno(g_proto), _O_BINARY);
  _setmode(crf_fileno(stdin), _O_BINARY);
#endif
  error_logger = stderr;

  Frame in;
  uint32_t jl = 0, bl = 0;
  for (;;)
  {
    FrameRead got = ReadFrame(in, jl, bl);
    if (got == FrameRead::End) break;
    if (got == FrameRead::TooLarge)
    {
      WriteFrame(Refusal({"frame.too-large", "a frame announced " + std::to_string(jl) + " bytes of JSON and " + std::to_string(bl) +
                                             " of binary, more than any request carries; the stream is out of step, so the worker stops"}));
      return 4;
    }
    bool quit = false;
    Frame response;
    try { response = Dispatch(in, quit); }
    catch (const Refuse& r) { response = Refusal(r); }
    catch (const std::exception& e) { response = Refusal({"worker.exception", e.what()}); }
    catch (...) { response = Refusal({"worker.exception", "an unknown exception"}); }
    WriteFrame(response);
    if (quit) return 0;
  }
  return 0;
}
