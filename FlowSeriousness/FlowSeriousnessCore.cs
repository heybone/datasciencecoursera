// FlowSeriousnessCore.cs -- Flow Seriousness Layer, pure model. No NinjaTrader references; C# 7.3.
//
// One clock (the volume bar), one data source (bid/ask-stamped prints), five O(1) reads per bar:
//   lambda_s  session impact coefficient      ticks of price per contract of net aggression (150-bar regression through the origin)
//   effN      normalised efficiency           points per 100 contracts of the current window, divided by what the book pays (100 * tick * lambda)
//   P_m       permanence of a burst move      fraction of the burst move still present m bars later; LiveP updates on every tick
//   vpin      volume-clock imbalance          mean |bar delta| / bar volume over N bars (each constant-volume bar IS the bucket)
//   I_t       recursive same-side intensity   exponential-kernel burst intensity per side, no estimation
// plus tempo-z (bar duration z, negative = fast).
//
// Speed: the developing bar is included in every window, so the character headline changes on the tick that
// makes it true. A burst is visible the moment the window delta crosses the floor; absorption / paid is read on
// every tick; a give-back is flagged on the first tick price retraces the whole burst. Only the P3 / P10 labels
// wait for their bars, and they are labels, not decisions. Events are sealed at bar close so the record does not flicker.
//
// Preview only. Nothing here places orders or reads a strategy.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Keystone.Seriousness
{
    public enum Character
    {
        Warming = 0, TwoWay = 1,
        BuyersPaid = 2, SellersPaid = 3, BuyPartial = 4, SellPartial = 5,
        BuysAbsorbed = 6, SellsAbsorbed = 7, SellersExhausted = 8, BuyersExhausted = 9,
        BuyGivenBack = 10, SellGivenBack = 11
    }
    public enum ChangeKind { Initiative = 0, Absorption = 1, Exhaustion = 2, Failure = 3, Reversal = 4, Regime = 5, Intensity = 6 }
    public enum Verdict { Partial = 0, Absorbed = 1, Paid = 2, Exhaust = 3 }

    public sealed class Settings
    {
        public int BarVolume = 500;
        public double Tick = 0.25;
        public int EffBars = 3;                   // window = developing bar + (EffBars - 1) closed bars
        public int PermShort = 3, PermLong = 10;  // permanence labels, in bars after the seal
        public double MinBurstMoveTicks = 2;      // a burst that moved less than this is absorbed at birth: no permanence tracking, no give-back
        public int LambdaBars = 150, LambdaWarmup = 20;
        public double LambdaMin = 1e-6, LambdaMax = 1.0;
        public bool ResetLambdaAtRoll = false;
        public int MedianBars = 30;
        public double BurstMultiple = 2.5;        // floor = max(BurstFloor, BurstMultiple * median |delta| over MedianBars)
        public double BurstFloor = 300;           // ES 300, NQ 150 (9/8 calibration)
        public double AbsorbBelow = 0.5, ConfirmAbove = 1.0;
        public int VpinBars = 20;
        public int TempoBars = 100;
        public int IntensityTau = 10;
        public double HotMultiple = 1.5;
        public int ExhaustLookback = 12;
        public double ExhaustDeltaFraction = 0.5;
        public int RegimeMinSamples = 30;
        public int ReversalBars = 12;             // paid -> absorbed (same side) within this many bars = character flip
        public int InterestBars = 6;              // how long a change keeps the interest tag alive
        public int HoldMs = 800;                  // headline hold before a weaker state replaces a stronger one
        public double LevelToleranceTicks = 8;
        public int EventMemory = 400;
        public int StripBars = 60;
    }

    public sealed class Bar
    {
        public DateTime Start, End;
        public double Open, High, Low, Close, Volume, Delta, Classified, CumDelta;
        public double DurationSec { get { return Math.Max(0, (End - Start).TotalSeconds); } }
    }

    // Constant-volume bars from prints. A print larger than the remaining bar volume is split across bars.
    public sealed class VolumeBuilder
    {
        readonly int size; Bar current; double cum;
        public VolumeBuilder(int size) { this.size = Math.Max(1, size); }
        public Bar Current { get { return current; } }
        public double CumulativeDelta { get { return cum; } }
        public void ResetCumulative() { cum = 0; }
        public IEnumerable<Bar> Add(DateTime t, double price, double volume, int side)
        {
            while (volume > 0)
            {
                if (current == null) current = new Bar { Start = t, End = t, Open = price, High = price, Low = price, Close = price };
                double take = Math.Min(size - current.Volume, volume);
                current.High = Math.Max(current.High, price); current.Low = Math.Min(current.Low, price);
                current.Close = price; current.End = t; current.Volume += take; current.Delta += take * side;
                if (side != 0) current.Classified += take;
                cum += take * side; current.CumDelta = cum;
                volume -= take;
                if (current.Volume >= size) { Bar done = current; current = null; yield return done; }
            }
        }
    }

    public sealed class FlowEvent
    {
        public int Id, Direction, BarIndex;
        public Verdict Verdict;
        public DateTime Time;
        public double Price, Base, Peak, Delta, EffN;
        public double LiveP = double.NaN, P3 = double.NaN, P10 = double.NaN;
        public bool GivenBack, Alive = true, AtLevel;
        public double Level = double.NaN;
        public int BarsSince;
        public FlowEvent Clone() { return (FlowEvent)MemberwiseClone(); }
        public string VerdictText
        {
            get
            {
                if (Verdict == Verdict.Exhaust) return Direction > 0 ? "SELLERS EXHAUSTED" : "BUYERS EXHAUSTED";
                if (Verdict == Verdict.Absorbed) return Direction > 0 ? "BUYS ABSORBED" : "SELLS ABSORBED";
                if (Verdict == Verdict.Paid) return Direction > 0 ? "BUYERS PAID" : "SELLERS PAID";
                return Direction > 0 ? "BUY BURST PARTIAL" : "SELL BURST PARTIAL";
            }
        }
    }

    public sealed class CharacterChange
    {
        public int Id, Side, BarIndex;
        public ChangeKind Kind;
        public DateTime Time;
        public double Price;
        public string Text = "";
        public bool AtLevel;
    }

    // Immutable snapshot for rendering and export. Built once per tick on the data thread; read on the UI thread.
    public sealed class Reading
    {
        public DateTime Time, Et; public bool Rth;
        public double Price;
        public bool Warm; public string Status = "";
        public Character Character, Instant;
        public string Headline = "", Detail = "";
        public double WindowDelta, WindowMove, Eff = double.NaN, EffN = double.NaN, Floor;
        public bool Burst; public int BurstDir;
        public double Lambda = double.NaN, LambdaPts100 = double.NaN, LambdaPct = double.NaN; public bool LambdaValid; public int LambdaRegime;
        public double Vpin = double.NaN, VpinPct = double.NaN; public int VpinQuartile;
        public double TempoZ = double.NaN;
        public double IBuy, ISell, IBuyMean, ISellMean; public int HotSide;
        public double Coverage = double.NaN;
        public FlowEvent LastBurst, LastExhaust;
        public CharacterChange LastChange;
        public int InterestSide; public string InterestText = ""; public bool InterestAtLevel;
        public bool AtLevel; public double LevelPrice = double.NaN, LevelDistanceTicks = double.NaN;
        public byte[] Strip = new byte[0]; public float[] StripEff = new float[0];
        public int BarsClosed, NeedBars, LambdaSamples;
    }

    public sealed class Engine
    {
        public readonly Settings S;
        readonly VolumeBuilder builder;
        readonly Bar[] ring; int ringHead = -1, ringCount;
        readonly double[] lx, ly; int lHead, lCount; double sxy, sxx, lambda = double.NaN; bool lambdaValid;
        readonly double[] vAbs; int vHead, vCount; double vSum;
        readonly double[] tDur; int tHead, tCount; double tSum, tSq;
        readonly double[] cov; int cHead, cCount; double covSum;
        double iBuy, iSell, iBuyMean, iSellMean; int sessionBars;
        readonly List<double> lamSamples = new List<double>(), vpinSamples = new List<double>();
        int lamRegime, vpinQuartile, hotSide; double lamPct = double.NaN, vpinPct = double.NaN;
        double burstFloor;
        readonly List<FlowEvent> events = new List<FlowEvent>();
        readonly List<CharacterChange> changes = new List<CharacterChange>();
        volatile FlowEvent[] eventsView = new FlowEvent[0];
        volatile CharacterChange[] changesView = new CharacterChange[0];
        FlowEvent lastBurst, lastExhaust; int nextEventId, nextChangeId, barIndex;
        readonly byte[] strip; readonly float[] stripEff;
        Character displayed = Character.Warming, candidate = Character.Warming; DateTime candidateSince;
        double[] levels = new double[0];
        DateTime day; bool rth;
        public volatile Reading Latest;
        public event Action<Bar, Reading> BarClosed;
        public event Action<CharacterChange> Changed;

        public Engine(Settings settings)
        {
            S = settings;
            builder = new VolumeBuilder(S.BarVolume);
            int need = Math.Max(S.EffBars + S.ExhaustLookback + 2, Math.Max(S.LambdaBars, Math.Max(S.TempoBars, S.MedianBars)) + 2);
            ring = new Bar[Math.Max(64, need)];
            lx = new double[S.LambdaBars]; ly = new double[S.LambdaBars];
            vAbs = new double[S.VpinBars]; tDur = new double[S.TempoBars]; cov = new double[Math.Max(6, S.VpinBars)];
            strip = new byte[S.StripBars]; stripEff = new float[S.StripBars];
            burstFloor = S.BurstFloor;
            Latest = new Reading { Status = "Waiting for prints", NeedBars = NeedBars };
        }

        public FlowEvent[] Events { get { return eventsView; } }
        public CharacterChange[] Changes { get { return changesView; } }
        public int NeedBars { get { return Math.Max(S.EffBars + S.ExhaustLookback, S.LambdaWarmup + 1); } }
        public void SetLevels(double[] prices) { levels = prices ?? new double[0]; }

        public static DateTime TradingDay(DateTime et) { return et.TimeOfDay >= TimeSpan.FromHours(18) ? et.Date.AddDays(1) : et.Date; }
        public static bool IsRth(DateTime et) { return et.TimeOfDay >= TimeSpan.FromHours(9.5) && et.TimeOfDay < TimeSpan.FromHours(16); }

        Bar Closed(int i) { return i < ringCount ? ring[(ringHead - i + ring.Length) % ring.Length] : null; }

        // ---- input -------------------------------------------------------------------------------------------
        public void Tick(DateTime time, DateTime et, double price, double volume, int side)
        {
            if (volume <= 0 || price <= 0 || double.IsNaN(price)) return;
            DateTime td = TradingDay(et);
            if (td != day) RollSession(td);
            rth = IsRth(et);
            foreach (Bar b in builder.Add(time, price, volume, side)) OnBarClosed(b);
            Evaluate(time, et, price);
        }

        void RollSession(DateTime td)
        {
            day = td; sessionBars = 0; iBuyMean = iSellMean = 0; lamSamples.Clear(); vpinSamples.Clear();
            lamRegime = 0; vpinQuartile = 0; hotSide = 0; lamPct = vpinPct = double.NaN;
            if (S.ResetLambdaAtRoll) { lHead = 0; lCount = 0; sxy = sxx = 0; lambdaValid = false; lambda = double.NaN; }
        }

        // ---- per bar -----------------------------------------------------------------------------------------
        void OnBarClosed(Bar b)
        {
            Bar prev = Closed(0);
            ringHead = (ringHead + 1) % ring.Length; ring[ringHead] = b; if (ringCount < ring.Length) ringCount++;
            barIndex++; sessionBars++;

            // lambda_s: x = bar delta (contracts), y = bar move (ticks); regression through the origin over LambdaBars.
            if (prev != null)
            {
                double x = b.Delta, y = (b.Close - prev.Close) / S.Tick;
                if (lCount == S.LambdaBars) { sxy -= lx[lHead] * ly[lHead]; sxx -= lx[lHead] * lx[lHead]; } else lCount++;
                lx[lHead] = x; ly[lHead] = y; sxy += x * y; sxx += x * x; lHead = (lHead + 1) % S.LambdaBars;
                if (lCount >= S.LambdaWarmup && sxx > 0)
                {
                    double l = sxy / sxx;
                    if (l > S.LambdaMin && l < S.LambdaMax) { lambda = l; lambdaValid = true; }   // else hold the last valid value
                }
            }
            // burst floor: regime-relative, never below the absolute floor.
            if (ringCount >= 10)
            {
                int n = Math.Min(S.MedianBars, ringCount); double[] a = new double[n];
                for (int i = 0; i < n; i++) a[i] = Math.Abs(Closed(i).Delta);
                Array.Sort(a); double median = n % 2 == 1 ? a[n / 2] : (a[n / 2 - 1] + a[n / 2]) / 2;
                burstFloor = Math.Max(S.BurstFloor, S.BurstMultiple * median);
            }
            // vpin on the bar clock.
            double ad = Math.Abs(b.Delta) / Math.Max(1, b.Volume);
            if (vCount == S.VpinBars) vSum -= vAbs[vHead]; else vCount++;
            vAbs[vHead] = ad; vSum += ad; vHead = (vHead + 1) % S.VpinBars;
            // classification coverage over the last VpinBars bars.
            double c = b.Volume > 0 ? b.Classified / b.Volume : 0;
            if (cCount == cov.Length) covSum -= cov[cHead]; else cCount++;
            cov[cHead] = c; covSum += c; cHead = (cHead + 1) % cov.Length;
            // tempo: bar duration, rolling mean and sd.
            double d = b.DurationSec;
            if (tCount == S.TempoBars) { tSum -= tDur[tHead]; tSq -= tDur[tHead] * tDur[tHead]; } else tCount++;
            tDur[tHead] = d; tSum += d; tSq += d * d; tHead = (tHead + 1) % S.TempoBars;
            // intensity per side, exponential kernel, and its session mean.
            double decay = Math.Exp(-1.0 / Math.Max(1, S.IntensityTau));
            iBuy = iBuy * decay + (b.Delta >= burstFloor ? 1 : 0);
            iSell = iSell * decay + (b.Delta <= -burstFloor ? 1 : 0);
            iBuyMean += (iBuy - iBuyMean) / sessionBars; iSellMean += (iSell - iSellMean) / sessionBars;
            int hot = 0;
            if (sessionBars >= S.RegimeMinSamples)
            {
                bool bh = iBuyMean > 0 && iBuy >= S.HotMultiple * iBuyMean, sh = iSellMean > 0 && iSell >= S.HotMultiple * iSellMean;
                hot = bh && (!sh || iBuy >= iSell) ? 1 : sh ? -1 : 0;
            }
            if (hot != hotSide)
            {
                if (hot != 0) AddChange(ChangeKind.Intensity, 0, b, hot > 0 ? "BUY BURSTS CLUSTERING · I " + iBuy.ToString("0.0") + " vs " + iBuyMean.ToString("0.0")
                                                                      : "SELL BURSTS CLUSTERING · I " + iSell.ToString("0.0") + " vs " + iSellMean.ToString("0.0"));
                hotSide = hot;
            }
            // session-relative regimes: lambda tercile, vpin quartile.
            if (lambdaValid)
            {
                double pct = Percentile(lamSamples, lambda); AddSample(lamSamples, lambda); lamPct = pct;
                int regime = lamSamples.Count < S.RegimeMinSamples ? 0 : pct >= 2.0 / 3 ? 1 : pct < 1.0 / 3 ? -1 : 0;
                if (regime != lamRegime)
                {
                    if (regime != 0) AddChange(ChangeKind.Regime, 0, b, (regime > 0 ? "BOOK THIN · λ " : "BOOK THICK · λ ") + (100 * S.Tick * lambda).ToString("0.00") + " pts/100 · " + (regime > 0 ? "top" : "bottom") + " tercile");
                    lamRegime = regime;
                }
            }
            if (vCount >= 5)
            {
                double vp = vSum / vCount; double pct = Percentile(vpinSamples, vp); AddSample(vpinSamples, vp); vpinPct = pct;
                vpinQuartile = vpinSamples.Count < S.RegimeMinSamples ? 0 : 1 + (int)Math.Min(3, Math.Floor(pct * 4));
            }
            // permanence labels for alive events.
            foreach (FlowEvent e in events)
            {
                if (!e.Alive) continue;
                e.BarsSince = barIndex - e.BarIndex;
                double span = e.Peak - e.Base; bool tracked = Math.Abs(span) >= S.MinBurstMoveTicks * S.Tick;
                if (e.BarsSince == S.PermShort && tracked) e.P3 = ClampP((b.Close - e.Base) / span);
                if (e.BarsSince >= S.PermLong) { if (tracked) e.P10 = ClampP((b.Close - e.Base) / span); e.Alive = false; }
            }
            // exhaustion and bursts are sealed on the closed window.
            int k = S.EffBars;
            if (ringCount >= k + S.ExhaustLookback + 1)
            {
                double wd = 0, wh = double.MinValue, wl = double.MaxValue;
                for (int i = 0; i < k; i++) { Bar w = Closed(i); wd += w.Delta; wh = Math.Max(wh, w.High); wl = Math.Min(wl, w.Low); }
                double ph = double.MinValue, pl = double.MaxValue;
                for (int i = k; i < k + S.ExhaustLookback; i++) { Bar p = Closed(i); ph = Math.Max(ph, p.High); pl = Math.Min(pl, p.Low); }
                double baseClose = Closed(k).Close, mid = (wh + wl) / 2;
                int exDir = wl <= pl && wd >= burstFloor * S.ExhaustDeltaFraction && b.Close > mid ? 1
                          : wh >= ph && wd <= -burstFloor * S.ExhaustDeltaFraction && b.Close < mid ? -1 : 0;
                if (exDir != 0 && (lastExhaust == null || lastExhaust.Direction != exDir || barIndex - lastExhaust.BarIndex >= k))
                {
                    FlowEvent e = NewEvent(exDir, Verdict.Exhaust, b, baseClose, wd, double.NaN);
                    lastExhaust = e;
                    AddChange(ChangeKind.Exhaustion, exDir, b, e.VerdictText + " at " + F(b.Close) + " · Δ " + wd.ToString("+0;-0") + " already " + (exDir > 0 ? "bought" : "sold"));
                }
                if (Math.Abs(wd) >= burstFloor)
                {
                    int dir = Math.Sign(wd);
                    if (lastBurst == null || lastBurst.Direction != dir || barIndex - lastBurst.BarIndex >= k)
                    {
                        double eff = (b.Close - baseClose) / (wd / 100.0);
                        double effN = lambdaValid ? eff / (100 * S.Tick * lambda) : double.NaN;
                        Verdict v = double.IsNaN(effN) ? Verdict.Partial : effN < S.AbsorbBelow ? Verdict.Absorbed : effN >= S.ConfirmAbove ? Verdict.Paid : Verdict.Partial;
                        FlowEvent prevBurst = lastBurst;
                        FlowEvent e = NewEvent(dir, v, b, baseClose, wd, effN);
                        lastBurst = e;
                        string effTxt = double.IsNaN(effN) ? "effN —" : "effN " + effN.ToString("0.00");
                        if (v == Verdict.Absorbed) AddChange(ChangeKind.Absorption, -dir, b, e.VerdictText + " at " + F(b.Close) + " · " + wd.ToString("+0;-0") + " moved " + (b.Close - baseClose).ToString("+0.00;-0.00") + " · " + effTxt);
                        else if (v == Verdict.Paid) AddChange(ChangeKind.Initiative, dir, b, e.VerdictText + " at " + F(b.Close) + " · " + wd.ToString("+0;-0") + " moved " + (b.Close - baseClose).ToString("+0.00;-0.00") + " · " + effTxt);
                        // character flips against the previous burst
                        if (prevBurst != null && barIndex - prevBurst.BarIndex <= S.ReversalBars)
                        {
                            if (prevBurst.Verdict == Verdict.Paid && prevBurst.Direction == dir && v == Verdict.Absorbed)
                                AddChange(ChangeKind.Reversal, -dir, b, (dir > 0 ? "BUYERS PAID → BUYS ABSORBED" : "SELLERS PAID → SELLS ABSORBED") + " · character flip at " + F(b.Close));
                            else if (prevBurst.Verdict == Verdict.Absorbed && prevBurst.Direction == -dir && v == Verdict.Paid)
                                AddChange(ChangeKind.Reversal, dir, b, (dir > 0 ? "SELLS ABSORBED → BUYERS PAID" : "BUYS ABSORBED → SELLERS PAID") + " · character flip at " + F(b.Close));
                        }
                    }
                }
            }
            // strip: the character at the close, and effN at the close.
            byte code = (byte)displayed; float ef = (float)(Latest != null && Latest.Burst && !double.IsNaN(Latest.EffN) ? Latest.EffN : double.NaN);   // no burst = no dash
            Array.Copy(strip, 1, strip, 0, strip.Length - 1); strip[strip.Length - 1] = code;
            Array.Copy(stripEff, 1, stripEff, 0, stripEff.Length - 1); stripEff[stripEff.Length - 1] = ef;
            if (events.Count > S.EventMemory) events.RemoveRange(0, events.Count - S.EventMemory);
            if (changes.Count > S.EventMemory) changes.RemoveRange(0, changes.Count - S.EventMemory);
            eventsView = events.ToArray();
            Action<Bar, Reading> h = BarClosed; if (h != null) h(b, Latest);
        }

        FlowEvent NewEvent(int dir, Verdict v, Bar b, double baseClose, double delta, double effN)
        {
            double lvl; double dist;
            bool at = NearLevel(b.Close, out lvl, out dist) || NearLevel(dir > 0 ? b.Low : b.High, out lvl, out dist);
            FlowEvent e = new FlowEvent { Id = ++nextEventId, Direction = dir, Verdict = v, Time = b.End, Price = b.Close, Base = baseClose, Peak = b.Close,
                Delta = delta, EffN = effN, BarIndex = barIndex, AtLevel = at, Level = at ? lvl : double.NaN };
            events.Add(e); return e;
        }

        void AddChange(ChangeKind kind, int side, Bar b, string text)
        {
            double lvl, dist; bool at = NearLevel(b.Close, out lvl, out dist);
            CharacterChange c = new CharacterChange { Id = ++nextChangeId, Kind = kind, Side = side, Time = b.End, Price = b.Close, Text = text, BarIndex = barIndex, AtLevel = at };
            changes.Add(c); changesView = changes.ToArray();
            Action<CharacterChange> h = Changed; if (h != null) h(c);
        }

        static void AddSample(List<double> s, double v) { s.Add(v); if (s.Count > 3000) s.RemoveAt(0); }
        static double Percentile(List<double> s, double v)
        {
            if (s.Count == 0) return double.NaN;
            int below = 0; for (int i = 0; i < s.Count; i++) if (s[i] < v) below++;
            return (double)below / s.Count;
        }
        bool NearLevel(double price, out double level, out double distanceTicks)
        {
            level = double.NaN; distanceTicks = double.NaN; double best = double.MaxValue;
            for (int i = 0; i < levels.Length; i++)
            {
                double dt = Math.Abs(price - levels[i]) / S.Tick;
                if (dt < best) { best = dt; level = levels[i]; }
            }
            if (best == double.MaxValue) return false;
            distanceTicks = best; return best <= S.LevelToleranceTicks;
        }
        static string F(double p) { return p.ToString("0.00", CultureInfo.InvariantCulture); }
        static double ClampP(double p) { return Math.Max(-3, Math.Min(3, p)); }

        // ---- per tick ----------------------------------------------------------------------------------------
        void Evaluate(DateTime time, DateTime et, double price)
        {
            Reading r = new Reading { Time = time, Et = et, Rth = rth, Price = price, BarsClosed = ringCount, NeedBars = NeedBars, Floor = burstFloor };
            int k = S.EffBars;
            r.LambdaValid = lambdaValid; r.Lambda = lambda; r.LambdaPts100 = lambdaValid ? 100 * S.Tick * lambda : double.NaN;
            r.LambdaPct = lambdaValid ? lamPct : double.NaN; r.LambdaRegime = lamRegime; r.LambdaSamples = lamSamples.Count;
            r.Vpin = vCount > 0 ? vSum / vCount : double.NaN; r.VpinPct = vpinPct; r.VpinQuartile = vpinQuartile;
            if (tCount >= 30) { double m = tSum / tCount, variance = Math.Max(0, tSq / tCount - m * m), sd = Math.Sqrt(variance); Bar last = Closed(0); r.TempoZ = sd > 0 && last != null ? (last.DurationSec - m) / sd : double.NaN; }
            r.IBuy = iBuy; r.ISell = iSell; r.IBuyMean = iBuyMean; r.ISellMean = iSellMean; r.HotSide = hotSide;
            r.Coverage = cCount > 0 ? covSum / cCount : double.NaN;
            double lvl, dist; r.AtLevel = NearLevel(price, out lvl, out dist); r.LevelPrice = lvl; r.LevelDistanceTicks = dist;
            r.Strip = new byte[strip.Length]; Array.Copy(strip, r.Strip, strip.Length);
            r.StripEff = new float[stripEff.Length]; Array.Copy(stripEff, r.StripEff, stripEff.Length);

            bool ready = ringCount >= k + S.ExhaustLookback && lambdaValid;
            Character instant;
            if (!ready)
            {
                instant = Character.Warming;
                r.Status = "WARMING · " + ringCount + "/" + NeedBars + " bars" + (lambdaValid ? "" : " · λ " + lCount + "/" + S.LambdaWarmup);
                r.Headline = "WARMING"; r.Detail = r.Status;
            }
            else
            {
                r.Warm = true; r.Status = rth ? "RTH" : "ETH";
                Bar dev = builder.Current;
                double devDelta = dev == null ? 0 : dev.Delta, devHigh = dev == null ? price : Math.Max(dev.High, price), devLow = dev == null ? price : Math.Min(dev.Low, price);
                double wd = devDelta, wh = devHigh, wl = devLow;
                for (int i = 0; i < k - 1; i++) { Bar w = Closed(i); wd += w.Delta; wh = Math.Max(wh, w.High); wl = Math.Min(wl, w.Low); }
                double baseClose = Closed(k - 1).Close, move = price - baseClose;
                double ph = double.MinValue, pl = double.MaxValue;
                for (int i = k - 1; i < k - 1 + S.ExhaustLookback; i++) { Bar p = Closed(i); ph = Math.Max(ph, p.High); pl = Math.Min(pl, p.Low); }
                r.WindowDelta = wd; r.WindowMove = move;
                r.Eff = Math.Abs(wd) >= 1 ? move / (wd / 100.0) : double.NaN;
                r.EffN = !double.IsNaN(r.Eff) ? r.Eff / (100 * S.Tick * lambda) : double.NaN;
                r.Burst = Math.Abs(wd) >= burstFloor; r.BurstDir = r.Burst ? Math.Sign(wd) : 0;
                double mid = (wh + wl) / 2;
                int exDir = wl <= pl && wd >= burstFloor * S.ExhaustDeltaFraction && price > mid ? 1
                          : wh >= ph && wd <= -burstFloor * S.ExhaustDeltaFraction && price < mid ? -1 : 0;

                // live permanence of the last burst; give-back is flagged on the first tick it is true
                FlowEvent lb = lastBurst;
                if (lb != null && lb.Alive)
                {
                    double span = lb.Peak - lb.Base;
                    lb.LiveP = Math.Abs(span) >= S.MinBurstMoveTicks * S.Tick ? ClampP((price - lb.Base) / span) : double.NaN;
                    if (!lb.GivenBack && !double.IsNaN(lb.LiveP) && lb.LiveP < 0)
                    {
                        lb.GivenBack = true;
                        Bar marker = new Bar { End = time, Close = price, High = price, Low = price };
                        AddChange(ChangeKind.Failure, -lb.Direction, marker, (lb.Direction > 0 ? "BUY BURST GIVEN BACK" : "SELL BURST GIVEN BACK") + " · base " + F(lb.Base) + " · peak " + F(lb.Peak));
                        eventsView = events.ToArray();
                    }
                }
                string dtxt = wd.ToString("+0;-0"), mtxt = move.ToString("+0.00;-0.00"), etxt = double.IsNaN(r.EffN) ? "—" : r.EffN.ToString("0.00");
                if (lb != null && lb.Alive && lb.GivenBack)
                {
                    instant = lb.Direction > 0 ? Character.BuyGivenBack : Character.SellGivenBack;
                    r.Headline = lb.Direction > 0 ? "BUY BURST GIVEN BACK" : "SELL BURST GIVEN BACK";
                    r.Detail = "burst " + F(lb.Base) + " → " + F(lb.Peak) + " · price back through the base · P " + (double.IsNaN(lb.LiveP) ? "—" : lb.LiveP.ToString("0.00"));
                }
                else if (r.Burst && !double.IsNaN(r.EffN) && r.EffN < S.AbsorbBelow)
                {
                    instant = r.BurstDir > 0 ? Character.BuysAbsorbed : Character.SellsAbsorbed;
                    r.Headline = r.BurstDir > 0 ? "BUYS ABSORBED" : "SELLS ABSORBED";
                    r.Detail = dtxt + " " + (r.BurstDir > 0 ? "bought" : "sold") + " " + mtxt + " pts · effN " + etxt + " (< " + S.AbsorbBelow.ToString("0.0") + ")";
                }
                else if (exDir != 0)
                {
                    instant = exDir > 0 ? Character.SellersExhausted : Character.BuyersExhausted;
                    r.Headline = exDir > 0 ? "SELLERS EXHAUSTED" : "BUYERS EXHAUSTED";
                    r.Detail = (exDir > 0 ? "fresh low, delta already " : "fresh high, delta already ") + dtxt + " · close back " + (exDir > 0 ? "above" : "below") + " the window mid";
                }
                else if (r.Burst && !double.IsNaN(r.EffN) && r.EffN >= S.ConfirmAbove)
                {
                    instant = r.BurstDir > 0 ? Character.BuyersPaid : Character.SellersPaid;
                    r.Headline = r.BurstDir > 0 ? "BUYERS PAID" : "SELLERS PAID";
                    r.Detail = dtxt + " moved " + mtxt + " pts · effN " + etxt + " (≥ " + S.ConfirmAbove.ToString("0.0") + ") · book pays " + r.LambdaPts100.ToString("0.00") + " / 100";
                }
                else if (r.Burst)
                {
                    instant = r.BurstDir > 0 ? Character.BuyPartial : Character.SellPartial;
                    r.Headline = (r.BurstDir > 0 ? "BUY BURST" : "SELL BURST") + " · PARTIAL RESPONSE";
                    r.Detail = dtxt + " moved " + mtxt + " pts · effN " + etxt + " · between " + S.AbsorbBelow.ToString("0.0") + " and " + S.ConfirmAbove.ToString("0.0");
                }
                else
                {
                    instant = Character.TwoWay;
                    r.Headline = "TWO-WAY · NO INITIATIVE";
                    r.Detail = "window Δ " + dtxt + " below the floor " + burstFloor.ToString("0") + " · move " + mtxt;
                }
            }
            r.Instant = instant;
            r.Character = Hold(instant, time);
            if (r.Character != instant) { r.Headline = HeadlineFor(r.Character) + "  ·  now " + HeadlineFor(instant).ToLowerInvariant(); }
            r.LastBurst = lastBurst == null ? null : lastBurst.Clone();
            r.LastExhaust = lastExhaust == null ? null : lastExhaust.Clone();
            CharacterChange lc = changes.Count > 0 ? changes[changes.Count - 1] : null;
            r.LastChange = lc;
            if (lc != null && lc.Side != 0 && barIndex - lc.BarIndex <= S.InterestBars)
            {
                r.InterestSide = lc.Side; r.InterestAtLevel = lc.AtLevel || r.AtLevel;
                string why = lc.Kind == ChangeKind.Reversal ? "character flip" : lc.Kind == ChangeKind.Absorption ? "absorption" : lc.Kind == ChangeKind.Exhaustion ? "exhaustion"
                           : lc.Kind == ChangeKind.Failure ? "give-back" : lc.Kind == ChangeKind.Initiative ? "continuation" : lc.Kind.ToString().ToLowerInvariant();
                r.InterestText = (lc.Side > 0 ? "LONG" : "SHORT") + " · " + why + (r.InterestAtLevel ? " · at level" + (double.IsNaN(r.LevelPrice) ? "" : " " + F(r.LevelPrice)) : " · in the air");
            }
            Latest = r;
        }

        Character Hold(Character instant, DateTime now)
        {
            if (instant == displayed) { candidate = instant; return displayed; }
            if (instant != candidate) { candidate = instant; candidateSince = now; }
            bool escalate = Priority(instant) > Priority(displayed);
            if (escalate || (now - candidateSince).TotalMilliseconds >= S.HoldMs || displayed == Character.Warming) displayed = instant;
            return displayed;
        }
        static int Priority(Character c)
        {
            switch (c)
            {
                case Character.BuyGivenBack: case Character.SellGivenBack: return 6;
                case Character.BuysAbsorbed: case Character.SellsAbsorbed: return 5;
                case Character.SellersExhausted: case Character.BuyersExhausted: return 4;
                case Character.BuyersPaid: case Character.SellersPaid: return 3;
                case Character.BuyPartial: case Character.SellPartial: return 2;
                case Character.TwoWay: return 1;
                default: return 0;
            }
        }
        public static string HeadlineFor(Character c)
        {
            switch (c)
            {
                case Character.BuyersPaid: return "BUYERS PAID";
                case Character.SellersPaid: return "SELLERS PAID";
                case Character.BuyPartial: return "BUY BURST · PARTIAL";
                case Character.SellPartial: return "SELL BURST · PARTIAL";
                case Character.BuysAbsorbed: return "BUYS ABSORBED";
                case Character.SellsAbsorbed: return "SELLS ABSORBED";
                case Character.SellersExhausted: return "SELLERS EXHAUSTED";
                case Character.BuyersExhausted: return "BUYERS EXHAUSTED";
                case Character.BuyGivenBack: return "BUY BURST GIVEN BACK";
                case Character.SellGivenBack: return "SELL BURST GIVEN BACK";
                case Character.TwoWay: return "TWO-WAY";
                default: return "WARMING";
            }
        }
        // Side a character argues for: +1 long, -1 short, 0 none.
        public static int SideOf(Character c)
        {
            switch (c)
            {
                case Character.SellsAbsorbed: case Character.SellersExhausted: case Character.SellGivenBack: case Character.BuyersPaid: return 1;
                case Character.BuysAbsorbed: case Character.BuyersExhausted: case Character.BuyGivenBack: case Character.SellersPaid: return -1;
                default: return 0;
            }
        }
    }
}
