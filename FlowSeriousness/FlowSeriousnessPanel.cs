// FlowSeriousnessPanel.cs -- NinjaTrader 8 adapter and SharpDX dashboard for the Flow Seriousness Layer (preview in isolation).
//
// Chart: the ES 500-volume (NQ 300-volume) chart. A 1-tick secondary series delivers every print with its own bid/ask,
// historically and live, so no Tick Replay is required. The engine builds its own constant-volume bars from those prints,
// exactly like AuctionEdge, and every read updates on every tick with the developing bar included.
//
// Outputs: transparent plots a strategy can read (EffN, LambdaPts100, Permanence, Vpin, IntensityBuy, IntensitySell,
// CharacterCode, Interest, AtLevel), the dashboard, price-panel markers at sealed events, and an optional per-bar CSV
// for the graders. Levels: user-drawn horizontal lines, rectangles and Y regions on the chart are read as levels.
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using SharpDX;
using SharpDX.DirectWrite;
using KS = Keystone.Seriousness;
using DxBrush = SharpDX.Direct2D1.SolidColorBrush;
#endregion

namespace NinjaTrader.NinjaScript.Indicators
{
    public class FlowSeriousnessPanel : Indicator
    {
        public const string Version = "1.1";   // shown in the panel footer so a stale assembly is obvious
        private KS.Engine engine;
        private TimeZoneInfo eastern, platformZone;
        private DateTime lastLevelScan;
        private string exportPath;
        private readonly StringBuilder csv = new StringBuilder();
        private int exportErrors;
        private double tickSize = 0.25;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "FlowSeriousnessPanel";
                Description = "Flow Seriousness Layer preview " + Version + ": lambda, normalised efficiency, permanence, vpin, intensity and the character headline on the volume-bar clock.";
                Calculate = Calculate.OnEachTick;
                IsOverlay = true; DrawOnPricePanel = true; DisplayInDataBox = true;
                IsSuspendedWhileInactive = false; BarsRequiredToPlot = 0; PaintPriceMarkers = false;
                BarVolume = 500; EffBars = 3; PermShort = 3; PermLong = 10; MinBurstMoveTicks = 2;
                LambdaBars = 150; LambdaWarmup = 20; ResetLambdaAtRoll = false;
                BurstFloor = 300; BurstMultiple = 2.5; AbsorbBelow = 0.5; ConfirmAbove = 1.0;
                VpinBars = 20; TempoBars = 100; IntensityTau = 10; HotMultiple = 1.5; ExhaustLookback = 12;
                ReversalBars = 12; InterestBars = 6; HoldMilliseconds = 800;
                UseChartLevels = true; LevelToleranceTicks = 8;
                PanelWidth = 640; DashboardHeight = 420; ShowMarkers = true; ShowStrip = true;
                ExportCsv = false;
                AddPlot(Brushes.Transparent, "EffN"); AddPlot(Brushes.Transparent, "LambdaPts100"); AddPlot(Brushes.Transparent, "Permanence");
                AddPlot(Brushes.Transparent, "Vpin"); AddPlot(Brushes.Transparent, "IntensityBuy"); AddPlot(Brushes.Transparent, "IntensitySell");
                AddPlot(Brushes.Transparent, "CharacterCode"); AddPlot(Brushes.Transparent, "Interest"); AddPlot(Brushes.Transparent, "AtLevel");
            }
            else if (State == State.Configure)
            {
                AddDataSeries(BarsPeriodType.Tick, 1);
            }
            else if (State == State.DataLoaded)
            {
                eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                platformZone = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
                tickSize = Instrument.MasterInstrument.TickSize;
                KS.Settings s = new KS.Settings
                {
                    BarVolume = BarVolume, Tick = tickSize, EffBars = EffBars, PermShort = PermShort, PermLong = PermLong, MinBurstMoveTicks = MinBurstMoveTicks,
                    LambdaBars = LambdaBars, LambdaWarmup = LambdaWarmup, ResetLambdaAtRoll = ResetLambdaAtRoll,
                    BurstFloor = BurstFloor, BurstMultiple = BurstMultiple, AbsorbBelow = AbsorbBelow, ConfirmAbove = ConfirmAbove,
                    VpinBars = VpinBars, TempoBars = TempoBars, IntensityTau = IntensityTau, HotMultiple = HotMultiple, ExhaustLookback = ExhaustLookback,
                    ReversalBars = ReversalBars, InterestBars = InterestBars, HoldMs = HoldMilliseconds, LevelToleranceTicks = LevelToleranceTicks
                };
                engine = new KS.Engine(s);
                if (ExportCsv)
                {
                    try
                    {
                        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FlowSeriousness_exports");
                        Directory.CreateDirectory(dir);
                        string sym = Instrument == null ? "unknown" : Instrument.MasterInstrument.Name;
                        exportPath = Path.Combine(dir, "FS_" + sym + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
                        File.WriteAllText(exportPath, "kind,time,et,price,delta,cum_delta,volume,classified,lambda_ticks,lambda_pts100,lambda_pct,effn,window_delta,floor,vpin,vpin_pct,tempo_z,i_buy,i_sell,hot,character,at_level,level,side,text,p3,p10\n");
                        engine.BarClosed += OnBarClosedExport; engine.Changed += OnChangedExport;
                    }
                    catch (Exception e) { exportPath = null; Print(Name + " export unavailable: " + e.Message); }
                }
            }
            else if (State == State.Terminated) { FlushCsv(); }
        }

        protected override void OnBarUpdate()
        {
            if (engine == null) return;
            if (BarsInProgress == 1)
            {
                if (CurrentBars[1] < 0) return;
                double price = Closes[1][0], volume = Volumes[1][0];
                double bid = BarsArray[1].GetBid(CurrentBars[1]), ask = BarsArray[1].GetAsk(CurrentBars[1]);
                int side = 0;
                if (bid > 0 && ask > bid) { if (price >= ask) side = 1; else if (price <= bid) side = -1; }   // inside the spread stays unclassified
                DateTime t = Times[1][0];
                DateTime et = TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(t, DateTimeKind.Unspecified), platformZone, eastern);
                if (UseChartLevels && Math.Abs((t - lastLevelScan).TotalSeconds) >= 5) { lastLevelScan = t; ScanLevels(); }
                engine.Tick(t, et, price, volume, side);
                return;
            }
            if (BarsInProgress != 0 || CurrentBar < 0) return;
            KS.Reading r = engine.Latest; if (r == null) return;
            Values[0][0] = double.IsNaN(r.EffN) ? 0 : r.EffN;
            Values[1][0] = double.IsNaN(r.LambdaPts100) ? 0 : r.LambdaPts100;
            Values[2][0] = r.LastBurst == null || double.IsNaN(r.LastBurst.LiveP) ? 0 : r.LastBurst.LiveP;
            Values[3][0] = double.IsNaN(r.Vpin) ? 0 : r.Vpin;
            Values[4][0] = r.IBuy; Values[5][0] = r.ISell;
            Values[6][0] = (int)r.Character;
            Values[7][0] = r.InterestSide;
            Values[8][0] = r.AtLevel ? 1 : 0;
        }

        private void ScanLevels()
        {
            try
            {
                List<double> list = new List<double>();
                foreach (object obj in DrawObjects)
                {
                    NinjaTrader.NinjaScript.DrawingTools.HorizontalLine hl = obj as NinjaTrader.NinjaScript.DrawingTools.HorizontalLine;
                    if (hl != null) { list.Add(hl.StartAnchor.Price); continue; }
                    NinjaTrader.NinjaScript.DrawingTools.Rectangle rc = obj as NinjaTrader.NinjaScript.DrawingTools.Rectangle;
                    if (rc != null) { list.Add(rc.StartAnchor.Price); list.Add(rc.EndAnchor.Price); continue; }
                    NinjaTrader.NinjaScript.DrawingTools.RegionHighlightY rg = obj as NinjaTrader.NinjaScript.DrawingTools.RegionHighlightY;
                    if (rg != null) { list.Add(rg.StartAnchor.Price); list.Add(rg.EndAnchor.Price); continue; }
                }
                engine.SetLevels(list.ToArray());
            }
            catch { }
        }

        // ---- export --------------------------------------------------------------------------------------------
        private static string N(double v, string f) { return double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString(f, CultureInfo.InvariantCulture); }
        private static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "'") + "\""; }
        private void OnBarClosedExport(KS.Bar b, KS.Reading r)
        {
            if (exportPath == null || r == null) return;
            csv.Append("BAR,").Append(b.End.ToString("s")).Append(',').Append(r.Et.ToString("s")).Append(',').Append(N(b.Close, "0.00")).Append(',')
               .Append(N(b.Delta, "0")).Append(',').Append(N(b.CumDelta, "0")).Append(',').Append(N(b.Volume, "0")).Append(',').Append(N(b.Classified, "0")).Append(',')
               .Append(N(r.Lambda, "0.000000")).Append(',').Append(N(r.LambdaPts100, "0.000")).Append(',').Append(N(r.LambdaPct, "0.00")).Append(',')
               .Append(N(r.EffN, "0.000")).Append(',').Append(N(r.WindowDelta, "0")).Append(',').Append(N(r.Floor, "0")).Append(',')
               .Append(N(r.Vpin, "0.000")).Append(',').Append(N(r.VpinPct, "0.00")).Append(',').Append(N(r.TempoZ, "0.00")).Append(',')
               .Append(N(r.IBuy, "0.00")).Append(',').Append(N(r.ISell, "0.00")).Append(',').Append(r.HotSide).Append(',')
               .Append(r.Character).Append(',').Append(r.AtLevel ? 1 : 0).Append(',').Append(N(r.LevelPrice, "0.00")).Append(",,,,\n");
            foreach (KS.FlowEvent e in engine.Events)
                if (e.BarsSince == PermShort || e.BarsSince == PermLong)
                    csv.Append("PERM,").Append(b.End.ToString("s")).Append(',').Append(r.Et.ToString("s")).Append(',').Append(N(e.Price, "0.00")).Append(',')
                       .Append(N(e.Delta, "0")).Append(",,,,,,,").Append(N(e.EffN, "0.000")).Append(",,,,,,,,,").Append(e.VerdictText).Append(',')
                       .Append(e.AtLevel ? 1 : 0).Append(',').Append(N(e.Level, "0.00")).Append(',').Append(e.Direction).Append(',').Append(Q("event " + e.Id + " · " + e.BarsSince + " bars"))
                       .Append(',').Append(N(e.P3, "0.00")).Append(',').Append(N(e.P10, "0.00")).Append('\n');
            FlushCsv();
        }
        private void OnChangedExport(KS.CharacterChange c)
        {
            if (exportPath == null) return;
            csv.Append("CHANGE,").Append(c.Time.ToString("s")).Append(",,").Append(N(c.Price, "0.00")).Append(",,,,,,,,,,,,,,,,,,")
               .Append(c.Kind).Append(',').Append(c.AtLevel ? 1 : 0).Append(",,").Append(c.Side).Append(',').Append(Q(c.Text)).Append(",,\n");
        }
        private void FlushCsv()
        {
            if (exportPath == null || csv.Length == 0) return;
            try { File.AppendAllText(exportPath, csv.ToString()); csv.Length = 0; }
            catch (Exception e) { if (++exportErrors <= 3) Print(Name + " export write failed: " + e.Message); }
        }

        // ---- rendering ----------------------------------------------------------------------------------------
        protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
        {
            if (RenderTarget == null || ChartPanel == null || IsInHitTest || engine == null) return;
            KS.Reading r = engine.Latest; if (r == null) return;
            KS.FlowEvent[] events = engine.Events; KS.CharacterChange[] changes = engine.Changes;
            // Layout, top to bottom: header, headline, mechanism, detail, three gauge cards, the strip, the ribbon, the footer.
            // The box sizes itself to this stack; DashboardHeight and the chart panel only cap it. The strip goes first when
            // space is short, then ribbon rows.
            const float rowH = 16f, gaugeH = 74f, stripH = 70f;
            float x = ChartPanel.X + 12, y = ChartPanel.Y + 10, w = Math.Min(ChartPanel.W - 24, PanelWidth);
            float cap = Math.Min(ChartPanel.H - 20, DashboardHeight);
            float fixedH = 8 + 34 + 26 + 16 + 16 + 6 + gaugeH + 12 + 16 + 6 + 18 + 4;   // everything except the strip and the ribbon rows
            bool strip = ShowStrip && r.Strip.Length > 0 && fixedH + stripH + rowH <= cap;
            int rows = (int)Math.Max(0, Math.Min(4, Math.Floor((cap - fixedH - (strip ? stripH : 0)) / rowH)));
            float h = Math.Min(cap, fixedH + (strip ? stripH : 0) + rows * rowH);
            using (DxBrush bg = Brush(0x0B1220)) using (DxBrush card = Brush(0x131F30)) using (DxBrush line = Brush(0x24364B))
            using (DxBrush text = Brush(0xE8F0F7)) using (DxBrush muted = Brush(0x91A6BB)) using (DxBrush teal = Brush(0x38D8BA))
            using (DxBrush coral = Brush(0xFF7E87)) using (DxBrush gold = Brush(0xE9C46A)) using (DxBrush violet = Brush(0xB69CFF))
            using (DxBrush dark = Brush(0x0B1220)) using (DxBrush tealDim = Brush(0x38D8BA, 0.35f)) using (DxBrush coralDim = Brush(0xFF7E87, 0.35f))
            using (DxBrush mutedDim = Brush(0x91A6BB, 0.25f))
            using (TextFormat titleFont = new TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", FontWeight.SemiBold, FontStyle.Normal, 21))
            using (TextFormat headFont = new TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", FontWeight.SemiBold, FontStyle.Normal, 18))
            using (TextFormat font = new TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", 12))
            using (TextFormat small = new TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", 10))
            using (TextFormat smallBold = new TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", FontWeight.SemiBold, FontStyle.Normal, 10))
            using (TextFormat smallRight = new TextFormat(NinjaTrader.Core.Globals.DirectWriteFactory, "Segoe UI", 10) { TextAlignment = TextAlignment.Trailing })
            {
                try
                {
                    if (ShowMarkers) DrawMarkers(chartControl, chartScale, events, changes, teal, coral, gold, violet, muted, dark, text, smallBold);
                    if (w < 300 || h < 120) return;
                    RenderTarget.PushAxisAlignedClip(new RectangleF(x, y, w, h), SharpDX.Direct2D1.AntialiasMode.PerPrimitive);
                    try
                    {
                        int side = KS.Engine.SideOf(r.Character);
                        DxBrush sideBrush = side > 0 ? teal : side < 0 ? coral : muted;
                        Rect(x, y, w, h, bg); Rect(x, y, 3, h, r.Warm ? sideBrush : gold);
                        float left = x + 17, innerW = w - 34, cy = y + 8;
                        // header
                        Text("SERIOUSNESS", left, cy, 150, 30, titleFont, text);
                        string session = (Instrument == null ? "" : Instrument.FullName + "  ·  ") + BarVolume + "v  ·  " + (r.Warm ? r.Status : "WARMING");
                        Text(session, x + 165, cy + 8, w - 180, 18, smallRight, r.Warm ? muted : gold);
                        cy += 34;
                        // headline row, with the interest pill on the right when a change argues a side
                        float pw = r.InterestSide != 0 ? Math.Min(w * 0.36f, 240) : 0, px = x + w - 17 - pw;
                        float headW = pw > 0 ? px - 8 - left : innerW;
                        string mech = Mechanism(r.Character);
                        Text(r.Headline, left, cy, headW, 26, headFont, r.Warm ? sideBrush : gold);
                        if (pw > 0)
                        {
                            DxBrush ib = r.InterestSide > 0 ? teal : coral;
                            if (r.InterestAtLevel) { Pill(px, cy + 2, pw, 22, ib); Text("ENTRY INTERESTING", px + 10, cy + 6, pw - 16, 16, smallBold, dark); }
                            else { PillOutline(px, cy + 2, pw, 22, ib); Text("INTERESTING · NOT AT LEVEL", px + 10, cy + 6, pw - 16, 16, smallBold, ib); }
                            Text(r.InterestText, px, cy + 27, pw, 16, smallRight, ib);
                        }
                        cy += 26;
                        Text(r.Warm && mech.Length > 0 ? mech : "", left, cy, headW, 16, small, muted); cy += 16;
                        Text(r.Detail, left, cy, headW, 16, small, muted); cy += 16 + 6;
                        // gauges
                        float gy = cy, gap = 8, cw = (innerW - 2 * gap) / 3, gx = left;
                        // 1 efficiency
                        Rect(gx, gy, cw, gaugeH, card); Rect(gx, gy, 2, gaugeH, r.Burst ? (r.BurstDir > 0 ? teal : coral) : muted);
                        Text("EFFICIENCY · " + EffBars + " bars" + (r.Burst ? "  ·  BURST" : ""), gx + 9, gy + 5, cw - 16, 14, small, r.Burst ? text : muted);
                        Gauge(gx + 9, gy + 22, cw - 18, r.EffN, -1, 2, new double[] { 0.5, 1.0 }, r.Burst ? (r.EffN < AbsorbBelow ? gold : r.EffN >= ConfirmAbove ? (r.BurstDir > 0 ? teal : coral) : muted) : mutedDim, line, muted, small);
                        Text((double.IsNaN(r.EffN) ? "effN —" : "effN " + r.EffN.ToString("0.00")) + "  ·  " + (double.IsNaN(r.Eff) ? "" : r.Eff.ToString("0.00") + " pt/100  ·  ") + "Δ " + r.WindowDelta.ToString("+0;-0") + (r.Burst ? "" : "  ·  floor " + r.Floor.ToString("0")),
                            gx + 9, gy + 52, cw - 16, 16, small, text);
                        // 2 permanence
                        float g2 = gx + cw + gap; KS.FlowEvent lb = r.LastBurst;
                        Rect(g2, gy, cw, gaugeH, card); Rect(g2, gy, 2, gaugeH, lb == null ? muted : lb.Verdict == KS.Verdict.Absorbed ? gold : lb.Direction > 0 ? teal : coral);
                        Text("PERMANENCE · " + (lb == null ? "no burst yet" : lb.VerdictText.ToLowerInvariant() + " " + lb.Price.ToString("0.00")), g2 + 9, gy + 5, cw - 16, 14, small, muted);
                        if (lb != null)
                        {
                            double pv = lb.Alive ? lb.LiveP : !double.IsNaN(lb.P10) ? lb.P10 : lb.P3;
                            Gauge(g2 + 9, gy + 22, cw - 18, pv, -1, 2, new double[] { 0, 0.5 }, lb.GivenBack ? violet : pv >= 0.5 ? (lb.Direction > 0 ? teal : coral) : gold, line, muted, small);
                            string seals = lb.Alive ? (lb.BarsSince < PermShort ? "P3 in " + (PermShort - lb.BarsSince) + " · P10 in " + (PermLong - lb.BarsSince) : "P10 in " + (PermLong - lb.BarsSince)) + " bars" : "sealed";
                            string pv1 = double.IsNaN(pv) ? (lb.Alive ? "move < " + MinBurstMoveTicks.ToString("0") + " ticks" : "—") : "P " + pv.ToString("0.00");
                            Text(pv1 + (lb.GivenBack ? "  GIVEN BACK" : "") + "  ·  P3 " + (double.IsNaN(lb.P3) ? "—" : lb.P3.ToString("0.00")) + "  ·  P10 " + (double.IsNaN(lb.P10) ? "—" : lb.P10.ToString("0.00")) + "  ·  " + seals,
                                g2 + 9, gy + 52, cw - 16, 16, small, text);
                        }
                        else Text("waiting for the first burst ≥ " + r.Floor.ToString("0") + " contracts", g2 + 9, gy + 30, cw - 16, 16, small, muted);
                        // 3 book and tape
                        float g3 = g2 + cw + gap;
                        Rect(g3, gy, cw, gaugeH, card); Rect(g3, gy, 2, gaugeH, r.LambdaRegime > 0 ? gold : r.LambdaRegime < 0 ? teal : muted);
                        Text("BOOK · TAPE", g3 + 9, gy + 5, cw - 16, 14, small, muted);
                        bool pctReady = !double.IsNaN(r.LambdaPct) && r.BarsClosed >= engine.S.RegimeMinSamples;
                        string lam = r.LambdaValid ? "λ " + r.LambdaPts100.ToString("0.00") + " pt/100  ·  " + (r.LambdaRegime > 0 ? "THIN" : r.LambdaRegime < 0 ? "THICK" : "NORMAL") + (pctReady ? "  " + (r.LambdaPct * 100).ToString("0") + "th" : "") : "λ warming";
                        Text(lam, g3 + 9, gy + 21, cw - 16, 16, small, text);
                        string tape = "vpin" + VpinBars + " " + (double.IsNaN(r.Vpin) ? "—" : r.Vpin.ToString("0.00")) + (r.VpinQuartile > 0 ? " Q" + r.VpinQuartile : "") + "  ·  tempo " + (double.IsNaN(r.TempoZ) ? "—" : r.TempoZ.ToString("+0.0;-0.0")) + (r.TempoZ <= -1 ? " FAST" : r.TempoZ >= 1 ? " SLOW" : "");
                        Text(tape, g3 + 9, gy + 37, cw - 16, 16, small, muted);
                        Text("cls " + (double.IsNaN(r.Coverage) ? "—" : (r.Coverage * 100).ToString("0") + "%") + "  ·  I" + (r.HotSide > 0 ? " BUY HOT" : r.HotSide < 0 ? " SELL HOT" : ""), g3 + 9, gy + 53, cw * 0.55f, 16, small, muted);
                        float ix = g3 + 9 + cw * 0.55f, iy = gy + 59, iw = cw - 18 - cw * 0.55f;
                        double imax = Math.Max(0.5, Math.Max(Math.Max(r.IBuy, r.ISell), Math.Max(r.IBuyMean, r.ISellMean)) * 1.4);
                        Rect(ix, iy, iw / 2 - 3, 4, line); Rect(ix, iy, (iw / 2 - 3) * (float)Math.Min(1, r.IBuy / imax), 4, r.HotSide > 0 ? teal : tealDim);
                        Rect(ix + iw / 2 + 3, iy, iw / 2 - 3, 4, line); Rect(ix + iw / 2 + 3, iy, (iw / 2 - 3) * (float)Math.Min(1, r.ISell / imax), 4, r.HotSide < 0 ? coral : coralDim);
                        cy = gy + gaugeH + 12;
                        // strip and sparkline
                        if (strip)
                        {
                            Text("LAST " + r.Strip.Length + " BARS · effN at close (guides 0.5 / 1.0) and the character", left, cy, innerW, 14, small, muted);
                            float sx0 = left, sw = innerW, sh = 34, cell = sw / r.Strip.Length, spY = cy + 17;
                            Rect(sx0, spY, sw, sh, card);
                            float y05 = spY + sh - (float)((0.5 + 1) / 3 * sh), y10 = spY + sh - (float)((1.0 + 1) / 3 * sh), y0 = spY + sh - (float)(1.0 / 3 * sh);
                            RenderTarget.DrawLine(new Vector2(sx0, y05), new Vector2(sx0 + sw, y05), line, 1); RenderTarget.DrawLine(new Vector2(sx0, y10), new Vector2(sx0 + sw, y10), line, 1);
                            RenderTarget.DrawLine(new Vector2(sx0, y0), new Vector2(sx0 + sw, y0), mutedDim, 1);
                            for (int i = 0; i < r.Strip.Length; i++)
                            {
                                KS.Character c = (KS.Character)r.Strip[i]; int cs = KS.Engine.SideOf(c);
                                bool strong = c == KS.Character.BuysAbsorbed || c == KS.Character.SellsAbsorbed || c == KS.Character.SellersExhausted || c == KS.Character.BuyersExhausted || c == KS.Character.BuyGivenBack || c == KS.Character.SellGivenBack || c == KS.Character.BuyersPaid || c == KS.Character.SellersPaid;
                                DxBrush cb = c == KS.Character.Warming ? line : cs > 0 ? (strong ? teal : tealDim) : cs < 0 ? (strong ? coral : coralDim) : c == KS.Character.TwoWay ? mutedDim : muted;
                                Rect(sx0 + i * cell, spY + sh + 3, Math.Max(1, cell - 1), 8, cb);
                                double ev = Math.Max(-1, Math.Min(2, r.StripEff[i]));
                                float ey = spY + sh - (float)((ev + 1) / 3 * sh);
                                if (c != KS.Character.Warming) Rect(sx0 + i * cell, ey - 1, Math.Max(1, cell - 1), 2, c == KS.Character.BuysAbsorbed || c == KS.Character.SellsAbsorbed ? gold : cb);
                            }
                            cy += stripH;
                        }
                        // ribbon of character changes
                        Text("CHARACTER CHANGES" + (changes.Length == 0 ? "  ·  none yet" : ""), left, cy, innerW, 14, small, muted);
                        float ry = cy + 16; int shown = 0;
                        for (int i = changes.Length - 1; i >= 0 && shown < rows; i--, shown++)
                        {
                            KS.CharacterChange c = changes[i];
                            DxBrush cb = c.Kind == KS.ChangeKind.Regime || c.Kind == KS.ChangeKind.Intensity ? muted : c.Side > 0 ? teal : c.Side < 0 ? coral : muted;
                            Text(c.Time.ToString("HH:mm:ss"), left, ry + shown * rowH, 60, 15, small, muted);
                            Text((c.Kind == KS.ChangeKind.Reversal ? "FLIP  " : "") + c.Text + (c.AtLevel ? "  · at level" : ""), left + 62, ry + shown * rowH, innerW - 62, 15, c.Kind == KS.ChangeKind.Reversal ? smallBold : small, cb);
                        }
                        Text("Flow Seriousness Layer · preview " + Version + " · box " + h.ToString("0") + " px · " + r.Time.ToString("HH:mm:ss"), left, y + h - 22, innerW, 16, small, muted);
                    }
                    finally { RenderTarget.PopAxisAlignedClip(); }
                }
                catch (Exception e) { if (++exportErrors <= 3) Print(Name + " render: " + e.Message); }
            }
        }

        private static string Mechanism(KS.Character c)
        {
            switch (c)
            {
                case KS.Character.BuysAbsorbed: case KS.Character.SellsAbsorbed: return "ABSORPTION · effort without result · argues the other side";
                case KS.Character.SellersExhausted: case KS.Character.BuyersExhausted: return "EXHAUSTION · fresh extreme with the delta already turned";
                case KS.Character.BuyGivenBack: case KS.Character.SellGivenBack: return "GIVE-BACK · the burst move did not stick";
                case KS.Character.BuyersPaid: case KS.Character.SellersPaid: return "INITIATIVE · the book is paying for the flow · continuation side";
                case KS.Character.BuyPartial: case KS.Character.SellPartial: return "INITIATIVE · partial response · unresolved";
                case KS.Character.TwoWay: return "NO INITIATIVE";
                default: return "";
            }
        }

        private void DrawMarkers(ChartControl chartControl, ChartScale chartScale, KS.FlowEvent[] events, KS.CharacterChange[] changes,
            DxBrush teal, DxBrush coral, DxBrush gold, DxBrush violet, DxBrush muted, DxBrush dark, DxBrush text, TextFormat smallBold)
        {
            float left = ChartPanel.X - 12, right = ChartPanel.X + ChartPanel.W + 12;
            foreach (KS.FlowEvent e in events)
            {
                float ex = (float)chartControl.GetXByTime(e.Time); if (ex < left || ex > right) continue;
                float ey = (float)chartScale.GetYByValue(e.Price);
                bool up = e.Direction > 0;
                if (e.Verdict == KS.Verdict.Exhaust)
                {
                    DxBrush b = up ? teal : coral; float cy = up ? ey + 12 : ey - 12;
                    RenderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new Vector2(ex, cy), 4, 4), b);
                    RenderTarget.DrawEllipse(new SharpDX.Direct2D1.Ellipse(new Vector2(ex, cy), 7, 7), b, 1);
                }
                else
                {
                    DxBrush b = e.Verdict == KS.Verdict.Absorbed ? gold : e.Verdict == KS.Verdict.Paid ? (up ? teal : coral) : muted;
                    float ty = up ? ey + 10 : ey - 10;
                    Tri(ex, ty, 6, up, b, e.Verdict != KS.Verdict.Partial);
                    if (e.GivenBack) Diamond(ex, ty + (up ? 12 : -12), 5, violet);
                }
                if (e.AtLevel) Rect(ex - 5, up ? ey + 21 : ey - 22, 10, 1.5f, text);
            }
            foreach (KS.CharacterChange c in changes)
            {
                if (c.Side == 0) continue;
                bool flag = c.Kind == KS.ChangeKind.Reversal || (c.AtLevel && (c.Kind == KS.ChangeKind.Absorption || c.Kind == KS.ChangeKind.Exhaustion || c.Kind == KS.ChangeKind.Failure));
                if (!flag) continue;
                float cx = (float)chartControl.GetXByTime(c.Time); if (cx < left || cx > right) continue;
                float cy = (float)chartScale.GetYByValue(c.Price);
                DxBrush b = c.Side > 0 ? teal : coral;
                float ly = c.Side > 0 ? cy + 26 : cy - 40;
                RenderTarget.DrawLine(new Vector2(cx, cy), new Vector2(cx, c.Side > 0 ? ly : ly + 14), b, 1);
                Pill(cx - 34, ly, 68, 14, b);
                Text(c.Kind == KS.ChangeKind.Reversal ? (c.Side > 0 ? "FLIP ▲" : "FLIP ▼") : (c.Side > 0 ? "LONG ▲" : "SHORT ▼"), cx - 30, ly + 1, 60, 13, smallBold, dark);
            }
        }

        // ---- SharpDX helpers ----------------------------------------------------------------------------------
        private DxBrush Brush(int rgb) { return Brush(rgb, 1f); }
        private DxBrush Brush(int rgb, float alpha) { return new DxBrush(RenderTarget, new Color4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, alpha)); }
        private void Rect(float x, float y, float w, float h, DxBrush brush) { RenderTarget.FillRectangle(new RectangleF(x, y, Math.Max(0, w), Math.Max(0, h)), brush); }
        private void Pill(float x, float y, float w, float h, DxBrush brush)
        { RenderTarget.FillRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = new RectangleF(x, y, Math.Max(0, w), Math.Max(0, h)), RadiusX = 4, RadiusY = 4 }, brush); }
        private void PillOutline(float x, float y, float w, float h, DxBrush brush)
        { RenderTarget.DrawRoundedRectangle(new SharpDX.Direct2D1.RoundedRectangle { Rect = new RectangleF(x, y, Math.Max(0, w), Math.Max(0, h)), RadiusX = 4, RadiusY = 4 }, brush, 1); }
        private void Text(string value, float x, float y, float w, float h, TextFormat format, DxBrush brush)
        { if (w > 0 && h > 0) RenderTarget.DrawText(value ?? "", format, new RectangleF(x, y, w, h), brush, SharpDX.Direct2D1.DrawTextOptions.Clip); }
        private void Tri(float x, float y, float size, bool up, DxBrush brush, bool filled)
        {
            using (SharpDX.Direct2D1.PathGeometry g = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory))
            {
                using (SharpDX.Direct2D1.GeometrySink s = g.Open())
                {
                    s.BeginFigure(new Vector2(x, up ? y - size : y + size), SharpDX.Direct2D1.FigureBegin.Filled);
                    s.AddLine(new Vector2(x + size, up ? y + size : y - size));
                    s.AddLine(new Vector2(x - size, up ? y + size : y - size));
                    s.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed); s.Close();
                }
                if (filled) RenderTarget.FillGeometry(g, brush); else RenderTarget.DrawGeometry(g, brush, 1.2f);
            }
        }
        private void Diamond(float x, float y, float size, DxBrush brush)
        {
            using (SharpDX.Direct2D1.PathGeometry g = new SharpDX.Direct2D1.PathGeometry(RenderTarget.Factory))
            {
                using (SharpDX.Direct2D1.GeometrySink s = g.Open())
                {
                    s.BeginFigure(new Vector2(x, y - size), SharpDX.Direct2D1.FigureBegin.Filled);
                    s.AddLine(new Vector2(x + size, y)); s.AddLine(new Vector2(x, y + size)); s.AddLine(new Vector2(x - size, y));
                    s.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed); s.Close();
                }
                RenderTarget.DrawGeometry(g, brush, 1.5f);
            }
        }
        // Horizontal gauge: track from lo..hi with guide marks, a value marker, and guide labels.
        private void Gauge(float x, float y, float w, double value, double lo, double hi, double[] guides, DxBrush marker, DxBrush track, DxBrush label, TextFormat small)
        {
            Rect(x, y + 4, w, 4, track);
            for (int i = 0; i < guides.Length; i++)
            {
                float gx = x + (float)((guides[i] - lo) / (hi - lo)) * w;
                Rect(gx, y, 1, 12, label);
                Text(guides[i].ToString("0.0"), gx - 12, y + 12, 24, 12, small, label);
            }
            if (double.IsNaN(value)) return;
            double v = Math.Max(lo, Math.Min(hi, value));
            float vx = x + (float)((v - lo) / (hi - lo)) * w;
            RenderTarget.FillEllipse(new SharpDX.Direct2D1.Ellipse(new Vector2(vx, y + 6), 5, 5), marker);
        }

        #region Properties
        [Browsable(false), XmlIgnore] public Series<double> EffN { get { return Values[0]; } }
        [Browsable(false), XmlIgnore] public Series<double> LambdaPts100 { get { return Values[1]; } }
        [Browsable(false), XmlIgnore] public Series<double> Permanence { get { return Values[2]; } }
        [Browsable(false), XmlIgnore] public Series<double> Vpin { get { return Values[3]; } }
        [Browsable(false), XmlIgnore] public Series<double> IntensityBuy { get { return Values[4]; } }
        [Browsable(false), XmlIgnore] public Series<double> IntensitySell { get { return Values[5]; } }
        [Browsable(false), XmlIgnore] public Series<double> CharacterCode { get { return Values[6]; } }
        [Browsable(false), XmlIgnore] public Series<double> Interest { get { return Values[7]; } }
        [Browsable(false), XmlIgnore] public Series<double> AtLevel { get { return Values[8]; } }

        [NinjaScriptProperty, Range(50, 50000), Display(Name = "Bar volume (engine clock)", GroupName = "1. Clock", Order = 0)] public int BarVolume { get; set; }
        [NinjaScriptProperty, Range(2, 10), Display(Name = "Efficiency window bars", GroupName = "1. Clock", Order = 1)] public int EffBars { get; set; }
        [NinjaScriptProperty, Range(1, 10), Display(Name = "Permanence short (bars)", GroupName = "1. Clock", Order = 2)] public int PermShort { get; set; }
        [NinjaScriptProperty, Range(2, 60), Display(Name = "Permanence long (bars)", GroupName = "1. Clock", Order = 3)] public int PermLong { get; set; }
        [NinjaScriptProperty, Range(1, 40), Display(Name = "Minimum burst move for permanence (ticks)", GroupName = "1. Clock", Order = 4)] public double MinBurstMoveTicks { get; set; }
        [NinjaScriptProperty, Range(30, 1000), Display(Name = "Lambda window bars", GroupName = "2. Reads", Order = 0)] public int LambdaBars { get; set; }
        [NinjaScriptProperty, Range(5, 200), Display(Name = "Lambda warm-up bars", GroupName = "2. Reads", Order = 1)] public int LambdaWarmup { get; set; }
        [NinjaScriptProperty, Display(Name = "Reset lambda at the 18:00 ET roll", GroupName = "2. Reads", Order = 2)] public bool ResetLambdaAtRoll { get; set; }
        [NinjaScriptProperty, Range(1, 100000), Display(Name = "Burst floor contracts (ES 300 / NQ 150)", GroupName = "2. Reads", Order = 3)] public double BurstFloor { get; set; }
        [NinjaScriptProperty, Range(1, 10), Display(Name = "Burst multiple of median |delta|", GroupName = "2. Reads", Order = 4)] public double BurstMultiple { get; set; }
        [NinjaScriptProperty, Range(0, 2), Display(Name = "Absorbed below effN", GroupName = "2. Reads", Order = 5)] public double AbsorbBelow { get; set; }
        [NinjaScriptProperty, Range(0.1, 5), Display(Name = "Confirmed at or above effN", GroupName = "2. Reads", Order = 6)] public double ConfirmAbove { get; set; }
        [NinjaScriptProperty, Range(5, 200), Display(Name = "Vpin bars", GroupName = "2. Reads", Order = 7)] public int VpinBars { get; set; }
        [NinjaScriptProperty, Range(30, 500), Display(Name = "Tempo bars", GroupName = "2. Reads", Order = 8)] public int TempoBars { get; set; }
        [NinjaScriptProperty, Range(2, 100), Display(Name = "Intensity tau (bars)", GroupName = "2. Reads", Order = 9)] public int IntensityTau { get; set; }
        [NinjaScriptProperty, Range(1, 5), Display(Name = "Hot intensity multiple of session mean", GroupName = "2. Reads", Order = 10)] public double HotMultiple { get; set; }
        [NinjaScriptProperty, Range(3, 60), Display(Name = "Exhaustion lookback bars", GroupName = "2. Reads", Order = 11)] public int ExhaustLookback { get; set; }
        [NinjaScriptProperty, Range(2, 60), Display(Name = "Character flip window bars", GroupName = "3. Character", Order = 0)] public int ReversalBars { get; set; }
        [NinjaScriptProperty, Range(1, 60), Display(Name = "Interest tag life (bars)", GroupName = "3. Character", Order = 1)] public int InterestBars { get; set; }
        [NinjaScriptProperty, Range(0, 10000), Display(Name = "Headline hold (ms)", GroupName = "3. Character", Order = 2)] public int HoldMilliseconds { get; set; }
        [NinjaScriptProperty, Display(Name = "Read chart lines / rectangles as levels", GroupName = "4. Levels", Order = 0)] public bool UseChartLevels { get; set; }
        [NinjaScriptProperty, Range(0, 100), Display(Name = "Level tolerance (ticks)", GroupName = "4. Levels", Order = 1)] public double LevelToleranceTicks { get; set; }
        [NinjaScriptProperty, Range(300, 1400), Display(Name = "Panel width", GroupName = "5. Display", Order = 0)] public int PanelWidth { get; set; }
        [NinjaScriptProperty, Range(160, 900), Display(Name = "Panel maximum height (the box sizes to its content)", GroupName = "5. Display", Order = 1)] public int DashboardHeight { get; set; }
        [NinjaScriptProperty, Display(Name = "Price markers at events", GroupName = "5. Display", Order = 2)] public bool ShowMarkers { get; set; }
        [NinjaScriptProperty, Display(Name = "Character strip and effN sparkline", GroupName = "5. Display", Order = 3)] public bool ShowStrip { get; set; }
        [NinjaScriptProperty, Display(Name = "Export per-bar CSV (Documents\\FlowSeriousness_exports)", GroupName = "6. Export", Order = 0)] public bool ExportCsv { get; set; }
        #endregion
    }
}
