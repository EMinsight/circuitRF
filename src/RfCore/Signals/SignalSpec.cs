// ================================================================
//  SignalSpec.cs  —  the description of a modulated test signal
//
//  A signal is a list of carriers (LTE, NR, CW, pulsed CW or an
//  imported I/Q file), each with its own bandwidth, modulation, frequency offset
//  from the source's centre frequency and relative power, plus record
//  length, clipping and spectral-shaping settings.  The spec is plain
//  data: it serialises to the .cmod JSON file the Signal Designer
//  writes and the netlist references, and its canonical JSON is the
//  cache key of the generated waveform.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RfCore.Signals
{
    public enum CarrierStandard { Lte, Nr, Cw, File, Pcw }

    public enum QamOrder { Qpsk = 2, Qam16 = 4, Qam64 = 6, Qam256 = 8, Qam1024 = 10 }

    public sealed record CarrierSpec
    {
        public CarrierStandard Standard { get; init; } = CarrierStandard.Lte;
        /// <summary>Channel bandwidth (Hz): 1.4/3/5/10/15/20 MHz for LTE; 5..100 MHz for NR.</summary>
        public double BandwidthHz { get; init; } = 20e6;
        /// <summary>NR subcarrier spacing (Hz): 15, 30 or 60 kHz.  Ignored for LTE (15 kHz).</summary>
        public double ScsHz { get; init; } = 30e3;
        public QamOrder Modulation { get; init; } = QamOrder.Qam64;
        /// <summary>Carrier offset from the source centre frequency (Hz).</summary>
        public double OffsetHz { get; init; }
        /// <summary>Carrier power relative to the other carriers (dB); the source's Pavg sets the total.</summary>
        public double PowerDb { get; init; }
        public int Seed { get; init; } = 1;
        /// <summary>I/Q file for <see cref="CarrierStandard.File"/> (Waveform_Generator .txt, or two-column I Q text).</summary>
        public string? FilePath { get; init; }
        /// <summary>Sample rate (Hz) of a <see cref="CarrierStandard.File"/> carrier's I/Q; null = the file's own
        /// <c># sample_rate_hz</c> header. Stated, it wins over the header — a file with none needs it.</summary>
        public double? SampleRateHz { get; init; }

        // A pulsed-CW carrier's timing (s); null = the default PulsedCw.Resolve applies. Null rather than defaulted so
        // the JSON, and with it the cache key, of every other carrier is what it was before pCW existed.
        /// <summary>Pulse repetition interval: the record is one PRI.</summary>
        public double? PriSeconds { get; init; }
        /// <summary>Pulse width, 50 % to 50 %.</summary>
        public double? PulseWidthSeconds { get; init; }
        /// <summary>10-90 % times of the raised-cosine edges.</summary>
        public double? RiseSeconds { get; init; }
        public double? FallSeconds { get; init; }
        /// <summary>Off-time before the rising edge leaves 0; null centres the pulse in the PRI.</summary>
        public double? DelaySeconds { get; init; }
        /// <summary>The droop window, as fractions of the flat top (null = 0.1 and 0.9).</summary>
        public double? MeasureStart { get; init; }
        public double? MeasureEnd { get; init; }

        public string Label => Standard switch
        {
            CarrierStandard.Lte => $"LTE {BandwidthHz / 1e6:0.#} MHz {ModName(Modulation)}",
            CarrierStandard.Nr => $"NR {BandwidthHz / 1e6:0.#} MHz {ScsHz / 1e3:0} kHz {ModName(Modulation)}",
            CarrierStandard.Cw => "CW",
            CarrierStandard.Pcw => $"pCW {PulsedCw.FormatTime(PulsedCw.Resolve(this).Width)} / {PulsedCw.FormatTime(PulsedCw.Resolve(this).Pri)}",
            _ => $"File {System.IO.Path.GetFileName(FilePath ?? "")}",
        };

        public static string ModName(QamOrder m) => m switch
        {
            QamOrder.Qpsk => "QPSK",
            QamOrder.Qam16 => "16QAM",
            QamOrder.Qam64 => "64QAM",
            QamOrder.Qam256 => "256QAM",
            _ => "1024QAM",
        };
    }

    public sealed record SignalSpec
    {
        public List<CarrierSpec> Carriers { get; init; } = new() { new CarrierSpec() };
        /// <summary>Periodic record length (s); rounded to whole 0.5 ms slots for LTE/NR.</summary>
        public double RecordSeconds { get; init; } = 1e-3;
        /// <summary>Clip to this PAPR (dB) with iterative clip-and-filter; null = unclipped (natural OFDM PAPR).</summary>
        public double? ClipPaprDb { get; init; }
        /// <summary>Spectral-shaping floor (dBc per bin, relative to the in-band mean) outside the channel; null = off.</summary>
        public double? ShapingFloorDbc { get; init; } = -80;
        /// <summary>Raised-cosine symbol window as a fraction of the normal cyclic prefix.</summary>
        public double WindowFraction { get; init; } = 0.2;

        // -2: clipping meets its target as the PEAK at 4x oversampling (it clipped at the native rate before)
        // -3: peak cancellation instead of clip-and-filter (same PAPR, lower EVM)
        // -4: the clipping target is judged at 8x oversampling (4x let NR 100 peaks through between samples)
        // -5: a FILE carrier takes its bandwidth from the file (the cache stores it) and is clipped like a generated one
        public const string GeneratorVersion = "crf-signals-5";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

        public static SignalSpec FromJson(string json)
            => JsonSerializer.Deserialize<SignalSpec>(json, JsonOpts)
               ?? throw new FormatException("empty signal description");

        /// <summary>Cache key: SHA-256 over the canonical JSON and the generator version.</summary>
        public string CacheKey()
        {
            var canon = JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                Converters = { new JsonStringEnumConverter() },
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            }) + "|" + GeneratorVersion + FileStamps();
            var h = SHA256.HashData(Encoding.UTF8.GetBytes(canon));
            return Convert.ToHexString(h, 0, 12).ToLowerInvariant();
        }

        /// <summary>Each I/Q file's length and write time, so a file edited in place is read again rather than served
        /// from the cache under its unchanged path.</summary>
        private string FileStamps()
        {
            var sb = new StringBuilder();
            foreach (var c in Carriers.Where(c => c.Standard == CarrierStandard.File && !string.IsNullOrEmpty(c.FilePath)))
            {
                try
                {
                    var fi = new System.IO.FileInfo(c.FilePath!);
                    sb.Append('|').Append(fi.Exists ? $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}" : "missing");
                }
                catch (Exception) { sb.Append("|unreadable"); }
            }
            return sb.ToString();
        }

        /// <summary>
        /// <paramref name="carrierText"/> with its <c>File=</c> value passed through <paramref name="map"/> and every
        /// other field left exactly as written — how a stored (workspace-relative) path is resolved for a run, without
        /// re-formatting the description and dropping what the formatter does not write.
        /// </summary>
        public static string MapFilePath(string carrierText, Func<string, string> map)
        {
            var parts = carrierText.Split(';');
            for (int i = 1; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq > 0 && parts[i][..eq].Trim().Equals("File", StringComparison.OrdinalIgnoreCase))
                    parts[i] = parts[i][..(eq + 1)] + map(parts[i][(eq + 1)..].Trim().Trim('"'));
            }
            return string.Join(';', parts);
        }

        /// <summary>Instantaneous bandwidth: lowest carrier edge to highest carrier edge (Hz).</summary>
        public double InstantaneousBandwidthHz
        {
            get
            {
                if (Carriers.Count == 0) return 0;
                double lo = Carriers.Min(c => c.OffsetHz - c.BandwidthHz / 2);
                double hi = Carriers.Max(c => c.OffsetHz + c.BandwidthHz / 2);
                return hi - lo;
            }
        }

        public string Summary
            => Carriers.Count == 1
                ? Carriers[0].Label
                : $"{Carriers.Count} carriers, IBW {InstantaneousBandwidthHz / 1e6:0.#} MHz";

        /// <summary>
        /// Parses the compact inline carrier form used in netlists:
        /// <c>"NR;BW=20MHz;SCS=30kHz;Mod=64QAM;Off=-200MHz;P=0;Seed=3"</c>, or for an imported I/Q file
        /// <c>"FILE;File=iq/capture.txt;Fs=122.88MHz;Off=0;P=0"</c>, or for a pulse train
        /// <c>"PCW;PRI=100us;PW=50us;Rise=0.5us;Fall=0.5us;Delay=10us;Off=0;P=0"</c>. BW, SCS, Fs and Off take Hz, kHz,
        /// MHz or GHz after the number; a bare number is Hz (<c>BW=20e6</c>). PRI, PW, Rise, Fall and Delay take s, ms,
        /// us (or µs) or ns; a bare number is seconds.
        /// </summary>
        public static CarrierSpec ParseCarrier(string text)
        {
            var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) throw new FormatException("empty carrier description");
            var c = new CarrierSpec
            {
                Standard = parts[0].ToUpperInvariant() switch
                {
                    "LTE" => CarrierStandard.Lte,
                    "NR" => CarrierStandard.Nr,
                    "CW" => CarrierStandard.Cw,
                    "FILE" => CarrierStandard.File,
                    "PCW" => CarrierStandard.Pcw,
                    _ => throw new FormatException($"unknown carrier standard '{parts[0]}' (LTE, NR, CW, PCW or FILE)"),
                },
            };
            foreach (var p in parts.Skip(1))
            {
                int eq = p.IndexOf('=');
                if (eq <= 0) throw new FormatException($"carrier field '{p}' is not key=value");
                string k = p[..eq].Trim().ToUpperInvariant(), v = p[(eq + 1)..].Trim();
                double Num() => double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
                c = k switch
                {
                    "BW" => c with { BandwidthHz = ParseHz(k, v) },
                    "SCS" => c with { ScsHz = ParseHz(k, v) },
                    "OFF" or "OFFSET" => c with { OffsetHz = ParseHz(k, v) },
                    "P" or "POWER" => c with { PowerDb = Num() },
                    "SEED" => c with { Seed = (int)Num() },
                    "FILE" => c with { FilePath = v.Trim('"') },
                    "FS" => c with { SampleRateHz = ParseHz(k, v) },
                    "MOD" => c with { Modulation = ParseMod(v) },
                    "PRI" => c with { PriSeconds = PulseField(c, k, v) },
                    "PW" => c with { PulseWidthSeconds = PulseField(c, k, v) },
                    "RISE" => c with { RiseSeconds = PulseField(c, k, v) },
                    "FALL" => c with { FallSeconds = PulseField(c, k, v) },
                    "DELAY" => c with { DelaySeconds = PulseField(c, k, v) },
                    "MEASURESTART" => c with { MeasureStart = PulseFraction(c, k, v) },
                    "MEASUREEND" => c with { MeasureEnd = PulseFraction(c, k, v) },
                    _ => throw new FormatException($"unknown carrier field '{k}'"),
                };
            }
            // A pulse's bandwidth is its edges', never the 20 MHz a CarrierSpec defaults to.
            if (c.Standard == CarrierStandard.Pcw) c = c with { BandwidthHz = PulsedCw.BandwidthHz(PulsedCw.Validate(c)) };
            return c;
        }

        private static double PulseField(CarrierSpec c, string key, string v)
        {
            if (c.Standard != CarrierStandard.Pcw) throw new FormatException($"carrier field {key} belongs to a PCW carrier");
            return ParseSeconds(key, v);
        }

        private static double PulseFraction(CarrierSpec c, string key, string v)
        {
            if (c.Standard != CarrierStandard.Pcw) throw new FormatException($"carrier field {key} belongs to a PCW carrier");
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double f))
                throw new FormatException($"carrier field {key}={v} is not a fraction of the pulse top");
            return f;
        }

        private static readonly (string Unit, string Exponent)[] TimeUnits =
            [("ms", "e-3"), ("us", "e-6"), ("µs", "e-6"), ("ns", "e-9"), ("s", "")];

        /// <summary>A time field: a number, optionally followed by s, ms, us (µs) or ns, exact case as for
        /// <see cref="ParseHz"/>, and applied the same way, by re-reading the literal with its exponent.</summary>
        private static double ParseSeconds(string key, string v)
        {
            string num = v, exp = "";
            foreach (var (unit, e) in TimeUnits)
                if (v.EndsWith(unit, StringComparison.Ordinal)) { num = v[..^unit.Length].TrimEnd(); exp = e; break; }
            bool hasExponent = num.Contains('e', StringComparison.OrdinalIgnoreCase);
            if (!double.TryParse(hasExponent ? num : num + exp, NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
                throw new FormatException($"carrier field {key}={v} is not a time: a number with s, ms, us or ns");
            return hasExponent && exp.Length > 0 ? s * Math.Pow(10, int.Parse(exp[1..], CultureInfo.InvariantCulture)) : s;
        }

        /// <summary>A time as a carrier's time fields read it (a number with s, ms, us, µs or ns; bare = seconds), or false.</summary>
        public static bool TryParseSeconds(string text, out double seconds)
        {
            try { seconds = ParseSeconds("", text.Trim()); return true; }
            catch (FormatException) { seconds = 0; return false; }
        }

        /// <summary>Writes <paramref name="s"/> in <paramref name="unit"/> when that reads back as the same double, and as
        /// plain seconds otherwise.</summary>
        private static string FormatSeconds(double s, string unit)
        {
            if (s == 0) return "0";
            var inv = CultureInfo.InvariantCulture;
            // 100e-6 / 1e-6 is 100.00000000000001: the shortest spelling that parses back to the same double is the one written
            double scaled = s / double.Parse("1" + TimeUnits.First(u => u.Unit == unit).Exponent, inv);
            foreach (var f in new[] { "G15", "G16", "R" })
            {
                string text = scaled.ToString(f, inv);
                if (!text.Contains('E') && ParseSeconds("", text + unit) == s) return text + unit;
            }
            return s.ToString("R", inv);
        }

        private static readonly (string Unit, string Exponent)[] FrequencyUnits =
            [("GHz", "e9"), ("MHz", "e6"), ("kHz", "e3"), ("Hz", "")];

        /// <summary>A frequency field: a number, optionally followed by Hz, kHz, MHz or GHz (exact case: m is milli
        /// everywhere else in circuitRF). The prefix is applied by re-reading the literal with its exponent rather than
        /// by multiplying, which is what lets <c>20MHz</c> and <c>20e6</c> parse to the same double.</summary>
        private static double ParseHz(string key, string v)
        {
            string num = v, exp = "";
            foreach (var (unit, e) in FrequencyUnits)
                if (v.EndsWith(unit, StringComparison.Ordinal)) { num = v[..^unit.Length].TrimEnd(); exp = e; break; }
            bool hasExponent = num.Contains('e', StringComparison.OrdinalIgnoreCase);
            if (!double.TryParse(hasExponent ? num : num + exp, NumberStyles.Float, CultureInfo.InvariantCulture, out double hz))
                throw new FormatException($"carrier field {key}={v} is not a frequency: a number with Hz, kHz, MHz or GHz");
            return hasExponent && exp.Length > 0 ? hz * Math.Pow(10, int.Parse(exp[1..], CultureInfo.InvariantCulture)) : hz;
        }

        /// <summary>Writes <paramref name="hz"/> in <paramref name="unit"/> when that reads back as the same double, and
        /// as plain Hz otherwise — a carrier written by the Signal Designer never changes value on its way through text.</summary>
        private static string FormatHz(double hz, string unit)
        {
            if (hz == 0) return "0";
            var inv = CultureInfo.InvariantCulture;
            string exp = FrequencyUnits.First(u => u.Unit == unit).Exponent;
            string scaled = (hz / double.Parse("1" + exp, inv)).ToString("R", inv);
            return !scaled.Contains('E') && ParseHz("", scaled + unit) == hz ? scaled + unit : hz.ToString("R", inv);
        }

        public static QamOrder ParseMod(string v) => v.ToUpperInvariant() switch
        {
            "QPSK" => QamOrder.Qpsk,
            "16QAM" => QamOrder.Qam16,
            "64QAM" => QamOrder.Qam64,
            "256QAM" => QamOrder.Qam256,
            "1024QAM" => QamOrder.Qam1024,
            _ => throw new FormatException($"unknown modulation '{v}' (QPSK, 16QAM, 64QAM, 256QAM, 1024QAM)"),
        };

        public static string FormatCarrier(CarrierSpec c)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(c.Standard.ToString().ToUpperInvariant());
            if (c.Standard is CarrierStandard.Lte or CarrierStandard.Nr)
            {
                sb.Append(";BW=").Append(FormatHz(c.BandwidthHz, "MHz"));
                if (c.Standard == CarrierStandard.Nr) sb.Append(";SCS=").Append(FormatHz(c.ScsHz, "kHz"));
                sb.Append(";Mod=").Append(CarrierSpec.ModName(c.Modulation));
            }
            if (c.Standard == CarrierStandard.File)
            {
                sb.Append(";File=").Append(c.FilePath);
                if (c.SampleRateHz is double fs) sb.Append(";Fs=").Append(FormatHz(fs, "MHz"));
            }
            if (c.Standard == CarrierStandard.Pcw)
            {
                // only what is stated: an unstated Delay stays centred when PW or PRI is edited in the netlist
                foreach (var (key, value) in new[] { ("PRI", c.PriSeconds), ("PW", c.PulseWidthSeconds), ("Rise", c.RiseSeconds),
                                                     ("Fall", c.FallSeconds), ("Delay", c.DelaySeconds) })
                    if (value is double t) sb.Append(';').Append(key).Append('=').Append(FormatSeconds(t, "us"));
                if (c.MeasureStart is double ms) sb.Append(";MeasureStart=").Append(ms.ToString("R", inv));
                if (c.MeasureEnd is double me) sb.Append(";MeasureEnd=").Append(me.ToString("R", inv));
            }
            sb.Append(";Off=").Append(FormatHz(c.OffsetHz, "MHz"));
            sb.Append(";P=").Append(c.PowerDb.ToString("R", inv));
            // a pulse draws no random symbols
            if (c.Standard != CarrierStandard.Pcw) sb.Append(";Seed=").Append(c.Seed.ToString(inv));
            return sb.ToString();
        }
    }
}
