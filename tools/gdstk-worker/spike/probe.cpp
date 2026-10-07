// gdstk-probe -- the G0 spike's direct questions to gdstk (brief-oasis-gdstk.md §3, Q5 Q6 Q7 Q8).
// Copyright (c) circuitRF contributors. MIT. Scratch code: it links gdstk in-process, which the
// product never does (D1); it exists to ask gdstk what it holds without a protocol in between.
//
//   gdstk-probe q6 <dir>              integer DBU -> file -> gdstk -> integer DBU, both formats, both
//                                     grids, the extremes and 10^6 random values
//   gdstk-probe q7gen <dir>           the two scale files (a: 10^6 polygons in 100 cells; b: one cell,
//                                     a 1000 x 1000 array of rectangles as ONE shape repetition)
//   gdstk-probe q7read <file> <fmt>   read one file: time, peak memory, counts, expansion cost
//   gdstk-probe errptr <file.oas>     read_oas with and without an error-code pointer
//   gdstk-probe unit <grid_per_um>    the START unit write_oas writes for that grid

#include <gdstk/gdstk.hpp>

#include <chrono>
#include <cinttypes>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <random>
#include <string>
#include <vector>

#if defined(__APPLE__) || defined(__linux__)
  #include <sys/resource.h>
#endif

using namespace gdstk;
static const double kPi = 3.14159265358979323846;

static long PeakRssKb()
{
#if defined(__APPLE__)
  rusage u{};
  getrusage(RUSAGE_SELF, &u);
  return u.ru_maxrss / 1024;  // bytes on macOS
#elif defined(__linux__)
  rusage u{};
  getrusage(RUSAGE_SELF, &u);
  return u.ru_maxrss;  // kilobytes on Linux
#else
  return -1;
#endif
}

static Cell* NewCell(Library& lib, const char* name)
{
  Cell* c = (Cell*)allocate_clear(sizeof(Cell));
  c->name = copy_string(name, nullptr);
  lib.cell_array.append(c);
  return c;
}

static Polygon* Poly(Cell* c, uint32_t layer, std::initializer_list<Vec2> pts)
{
  Polygon* p = (Polygon*)allocate_clear(sizeof(Polygon));
  p->tag = make_tag(layer, 0);
  for (auto& v : pts) p->point_array.append(v);
  c->polygon_array.append(p);
  return p;
}

static Reference* Ref(Cell* parent, Cell* child, Vec2 at, double rotDeg, double mag, bool mirror)
{
  Reference* r = (Reference*)allocate_clear(sizeof(Reference));
  r->init(child);
  r->origin = at;
  r->rotation = rotDeg * kPi / 180;
  r->magnification = mag;
  r->x_reflection = mirror;
  parent->reference_array.append(r);
  return r;
}


// ---- Q7 -----------------------------------------------------------------------------------------

static double Seconds(std::chrono::steady_clock::time_point t0)
{
  return std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
}

// (a) 1,000,000 distinct polygons in 100 cells (10,000 each, every one a different hexagon), and a top
// cell placing each once; (b) one cell holding ONE rectangle with a 1000 x 1000 rectangular repetition.
static int Q7Gen(const std::string& dir)
{
  {
    Library lib = {};
    lib.init("LIB", 1e-6, 1e-9);
    Cell* top = NewCell(lib, "top");
    std::mt19937_64 rng(7);
    for (int c = 0; c < 100; c++)
    {
      char name[32];
      std::snprintf(name, sizeof name, "c%03d", c);
      Cell* cell = NewCell(lib, name);
      for (int i = 0; i < 10000; i++)
      {
        double x = (i % 100) * 10.0, y = (i / 100) * 10.0;  // um
        double d = (rng() % 4000) * 1e-3 + 1;               // 1..5 um, on the 1 nm grid
        Poly(cell, (uint32_t)(c % 8), {{x, y}, {x + d, y}, {x + d + 1, y + 1}, {x + d, y + 2}, {x, y + 2}, {x - 1, y + 1}});
      }
      Ref(top, cell, Vec2{(double)(c % 10) * 2000, (double)(c / 10) * 2000}, 0, 1, false);
    }
    auto t0 = std::chrono::steady_clock::now();
    ErrorCode e1 = lib.write_oas((dir + "/q7a.oas").c_str(), 0, 6, OASIS_CONFIG_DETECT_ALL | OASIS_CONFIG_INCLUDE_CRC32);
    double toas = Seconds(t0);
    t0 = std::chrono::steady_clock::now();
    ErrorCode e2 = lib.write_gds((dir + "/q7a.gds").c_str(), 0, nullptr);
    double tgds = Seconds(t0);
    std::printf("{\"file\":\"q7a\",\"write_oas_s\":%.3f,\"write_gds_s\":%.3f,\"errors\":[%d,%d]}\n", toas, tgds, (int)e1, (int)e2);
    lib.free_all();
  }
  {
    Library lib = {};
    lib.init("LIB", 1e-6, 1e-9);
    Cell* cell = NewCell(lib, "array");
    Polygon* p = (Polygon*)allocate_clear(sizeof(Polygon));
    *p = rectangle(Vec2{0, 0}, Vec2{1, 1}, make_tag(1, 0));
    p->repetition.type = RepetitionType::Rectangular;
    p->repetition.columns = 1000;
    p->repetition.rows = 1000;
    p->repetition.spacing = Vec2{2, 2};
    cell->polygon_array.append(p);
    ErrorCode e = lib.write_oas((dir + "/q7b.oas").c_str(), 0, 6, OASIS_CONFIG_DETECT_ALL | OASIS_CONFIG_INCLUDE_CRC32);
    std::printf("{\"file\":\"q7b\",\"error\":%d}\n", (int)e);
    lib.free_all();
  }
  return 0;
}

// Read as the worker reads (unit = the file's own precision), then report what the reply frames would
// carry and what expanding every shape repetition into single shapes would cost.
static int Q7Read(const std::string& file, const std::string& fmt)
{
  auto t0 = std::chrono::steady_clock::now();
  Library lib = {};
  ErrorCode err = ErrorCode::NoError;
  double precision = 0, unit = 0;
  if (fmt == "gds")
  {
    gds_units(file.c_str(), unit, precision);
    lib = read_gds(file.c_str(), precision, 0.5, nullptr, &err);
  }
  else
  {
    oas_precision(file.c_str(), precision);
    lib = read_oas(file.c_str(), precision, 0.5, nullptr);
  }
  double tread = Seconds(t0);
  long rssRead = PeakRssKb();
  uint64_t polys = 0, verts = 0, reps = 0, expanded = 0, refs = 0;
  for (uint64_t i = 0; i < lib.cell_array.count; i++)
  {
    Cell* c = lib.cell_array[i];
    refs += c->reference_array.count;
    for (uint64_t k = 0; k < c->polygon_array.count; k++)
    {
      Polygon* p = c->polygon_array[k];
      polys++;
      verts += p->point_array.count;
      uint64_t n = p->repetition.get_count();
      if (n > 1) { reps++; expanded += n; } else expanded += 1;
    }
  }
  // The expansion G4 would do for a shape repetition circuitRF cannot hold.
  t0 = std::chrono::steady_clock::now();
  uint64_t made = 0;
  for (uint64_t i = 0; i < lib.cell_array.count; i++)
  {
    Cell* c = lib.cell_array[i];
    Array<Polygon*> out = {};
    for (uint64_t k = 0; k < c->polygon_array.count; k++)
      if (c->polygon_array[k]->repetition.get_count() > 1) c->polygon_array[k]->apply_repetition(out);
    made += out.count;
    for (uint64_t k = 0; k < out.count; k++) { out[k]->clear(); free_allocation(out[k]); }
    out.clear();
  }
  double texpand = Seconds(t0);
  long rssExpand = PeakRssKb();
  std::printf("{\"file\":\"%s\",\"format\":\"%s\",\"error\":%d,\"read_s\":%.3f,\"peak_rss_kb_after_read\":%ld,\"cells\":%" PRIu64
              ",\"polygons\":%" PRIu64 ",\"vertices\":%" PRIu64 ",\"refs\":%" PRIu64 ",\"shape_repetitions\":%" PRIu64
              ",\"expanded_polygons\":%" PRIu64 ",\"expansion_made\":%" PRIu64 ",\"expand_s\":%.3f,\"peak_rss_kb_after_expand\":%ld}\n",
              file.c_str(), fmt.c_str(), (int)err, tread, rssRead, lib.cell_array.count, polys, verts, refs, reps, expanded, made, texpand,
              rssExpand);
  lib.free_all();
  return 0;
}

// read_oas's loop runs only while *error_code is NoError, and an XNAME is UnsupportedRecord, a WARNING:
// with a pointer it stops there; without one it reads to END.
static int ErrPtr(const std::string& file)
{
  for (int withPtr = 1; withPtr >= 0; withPtr--)
  {
    ErrorCode err = ErrorCode::NoError;
    Library lib = read_oas(file.c_str(), 0, 0, withPtr ? &err : nullptr);
    uint64_t polys = 0;
    for (uint64_t i = 0; i < lib.cell_array.count; i++) polys += lib.cell_array[i]->polygon_array.count;
    std::printf("{\"error_pointer\":%s,\"error\":%d,\"reached_end\":%s,\"cells\":%" PRIu64 ",\"polygons\":%" PRIu64 "}\n",
                withPtr ? "true" : "false", (int)err, lib.name ? "true" : "false", lib.cell_array.count, polys);
    lib.free_all();
  }
  return 0;
}

static int Unit(double grid)
{
  double precision = 1e-6 / grid;
  double written = 1e-6 / precision;  // what write_oas computes and hands to oasis_write_real
  bool integral = std::trunc(written) == written;
  double inverse = 1.0 / written;
  std::printf("{\"grid_per_um\":%.17g,\"precision\":%.17g,\"written_real\":%.17g,\"encoding\":\"%s\",\"read_back_precision\":%.17g}\n", grid,
              precision, written, integral ? "integer" : (std::trunc(inverse) == inverse ? "reciprocal" : "ieee-double"),
              1e-6 * (1 / written));
  return 0;
}

int main(int argc, char** argv)
{
  if (argc < 2) { std::fprintf(stderr, "usage: see probe.cpp\n"); return 2; }
  std::string cmd = argv[1];
  if (cmd == "q7gen" && argc == 3) return Q7Gen(argv[2]);
  if (cmd == "q7read" && argc == 4) return Q7Read(argv[2], argv[3]);
  if (cmd == "errptr" && argc == 3) return ErrPtr(argv[2]);
  if (cmd == "unit" && argc == 3) return Unit(std::atof(argv[2]));
  std::fprintf(stderr, "unknown command\n");
  return 2;
}
