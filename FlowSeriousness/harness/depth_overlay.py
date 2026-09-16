r"""AuctionEdgeDepth overlay (Kevin 2026-09-16: "overlay the data from this indicator with the ES chart; is there
anything to glean from this data?").

Reads the passive depth recorder's per-second file
    Documents\NinjaTrader 8\AuctionEdgeDepth\<contract>\<run>\samples.csv
(one row per second: rolling-window totals of book changes in a 10-point band each side, matched against trades,
plus last_price) and lines the book against price. Price is the recorder's own last_price, so no second file is
needed. Every run folder overlapping the window is read and concatenated (restarts are normal).

    python depth_overlay.py [YYYY-MM-DD] [ES|NQ] [HH:MM HH:MM]      chart-local (Arizona) times
    python depth_overlay.py 2026-09-16 ES 06:30 13:00               defaults: today, ES, the whole day

Definitions (all from the recorder's own columns, all rolling-window totals at that second):
    stack     net near-touch stacking = (near_bid_added - near_bid_unmatched) - (near_ask_added - near_ask_unmatched)
              positive = bids being built or held relative to offers within 2 points of the touch
    refill    refilled / executed per side = how much of what got hit came back within 2 s (defended liquidity)
    pull      unmatched reduction per side = size that left the book without a trade against it (the recorder
              itself says these are NOT proven cancellations; the feed's visible-depth boundary moves too)

Prints:
    health    share of seconds with a ready book and with the full band visible (Kinetick depth is intermittent)
    lead-lag  Pearson correlation of stack, refill difference and pull difference with the FORWARD price change at
              5 / 15 / 30 / 60 s, on ready seconds; both every-second (overlapping windows, inflated n) and every
              30 s (non-overlapping) so the n is honest
    pulls     price 30 s after the largest one-sided pull events (top 2 % of pull on one side while the other is quiet)
    zones     per zone visit: defending-side refill share on the way in vs how far price left the zone in 2 min
    picture   price with zone shading, stack, refill share bid/ask, pulls bid/ask
              -> reports\depth_overlay_<date>_<SYM>_<HHMM>-<HHMM>.png   (matplotlib; skipped if unavailable)
    minute    per-minute aggregate -> reports\depth_overlay_<date>_<SYM>.csv (small enough for the nightly snapshot)
Nothing here is a verdict. Small n is printed as small n.
"""
import csv, glob, os, sys, math, datetime as dt, statistics
from collections import defaultdict

DOCS = os.path.join(os.path.expanduser("~"), "Documents")
DEPTH = os.path.join(DOCS, "NinjaTrader 8", "AuctionEdgeDepth")
REPORTS = os.path.join(os.path.expanduser("~"), "Project KeystoneLevels", "reports")
MST = dt.timedelta(hours=7)          # Arizona, no DST: UTC = chart-local + 7 h
HORIZONS = (5, 15, 30, 60)


def piso(s):
    s = s.strip().rstrip("Z")
    if "+" in s[10:]: s = s[:s.rindex("+")]
    if "." in s:
        b, f = s.split(".", 1)
        s = b + "." + (f + "000000")[:6]
    return dt.datetime.fromisoformat(s)


def fnum(x):
    try: return float(x)
    except (TypeError, ValueError): return 0.0


def pearson(xs, ys):
    n = len(xs)
    if n < 3: return float("nan"), n
    mx, my = sum(xs) / n, sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs); syy = sum((y - my) ** 2 for y in ys)
    if sxx <= 0 or syy <= 0: return float("nan"), n
    return sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / math.sqrt(sxx * syy), n


def pct(xs, q):
    if not xs: return float("nan")
    s = sorted(xs); k = max(0, min(len(s) - 1, int(round(q * (len(s) - 1)))))
    return s[k]


def load(sym, day, t0, t1):
    """All samples rows of every run of the contract that overlap the window, sorted by observed time (chart-local)."""
    lo = dt.datetime.combine(day, t0) + MST; hi = dt.datetime.combine(day, t1) + MST
    rows = []
    for path in glob.glob(os.path.join(DEPTH, sym + "_*", "*", "samples.csv")):
        try:
            with open(path, newline="", encoding="utf-8") as f:
                rd = csv.DictReader(f)
                for r in rd:
                    try: t = piso(r["observed_utc"])
                    except (KeyError, ValueError): continue
                    if t < lo or t > hi: continue
                    r["_t"] = t - MST; r["_path"] = path
                    rows.append(r)
        except OSError:
            continue
    rows.sort(key=lambda r: r["_t"])
    # de-duplicate seconds across overlapping runs (a restart re-records the same second)
    out, last = [], None
    for r in rows:
        key = r["_t"].replace(microsecond=0)
        if last is not None and key == last: continue
        out.append(r); last = key
    return out


def series(rows):
    """Per-second derived series on the recorder's own columns."""
    S = []
    for r in rows:
        nb_add, nb_un = fnum(r["near_bid_added"]), fnum(r["near_bid_unmatched"])
        na_add, na_un = fnum(r["near_ask_added"]), fnum(r["near_ask_unmatched"])
        nb_ex, na_ex = fnum(r["near_bid_executed"]), fnum(r["near_ask_executed"])
        nb_rf, na_rf = fnum(r["near_bid_refilled"]), fnum(r["near_ask_refilled"])
        S.append({
            "t": r["_t"], "ready": r["ready"] == "1", "full": r["full_band"] == "1", "price": fnum(r["last_price"]),
            "stack": (nb_add - nb_un) - (na_add - na_un),
            "refill_bid": nb_rf / nb_ex if nb_ex > 0 else float("nan"), "refill_ask": na_rf / na_ex if na_ex > 0 else float("nan"),
            "pull_bid": fnum(r["bid_unmatched"]), "pull_ask": fnum(r["ask_unmatched"]),
            "exec_bid": nb_ex, "exec_ask": na_ex,
            "zone_low": fnum(r["zone_low"]), "zone_high": fnum(r["zone_high"]),
            "zb_ex": fnum(r["zone_bid_executed"]), "zb_rf": fnum(r["zone_bid_refilled"]), "za_ex": fnum(r["zone_ask_executed"]), "za_rf": fnum(r["zone_ask_refilled"]),
            "resets": int(fnum(r["resets"])), "rejected": int(fnum(r["rejected"])), "dropped": int(fnum(r["dropped"])),
        })
    return S


def forward(S, i, h):
    """Price change from second i to the first sample at least h seconds later, or None."""
    t = S[i]["t"] + dt.timedelta(seconds=h)
    j = i + 1
    while j < len(S) and S[j]["t"] < t: j += 1
    if j >= len(S) or (S[j]["t"] - t).total_seconds() > 5 or S[i]["price"] <= 0 or S[j]["price"] <= 0: return None
    return S[j]["price"] - S[i]["price"]


def leadlag(S, name, value, step):
    """Correlation of value(i) with the forward move at each horizon, sampling every `step` seconds."""
    out = []
    for h in HORIZONS:
        xs, ys, last = [], [], None
        for i, s in enumerate(S):
            if not s["ready"]: continue
            v = value(s)
            if v is None or (isinstance(v, float) and math.isnan(v)): continue
            if last is not None and (s["t"] - last).total_seconds() < step: continue
            fw = forward(S, i, h)
            if fw is None: continue
            xs.append(v); ys.append(fw); last = s["t"]
        c, n = pearson(xs, ys)
        out.append((h, c, n))
    return name, out


def main():
    args = sys.argv[1:]
    day = dt.date.today(); sym = "ES"; t0 = dt.time(0, 0); t1 = dt.time(23, 59, 59)
    if args and len(args[0]) == 10: day = dt.datetime.strptime(args.pop(0), "%Y-%m-%d").date()
    if args and args[0].upper() in ("ES", "NQ", "RTY", "CL", "GC"): sym = args.pop(0).upper()
    if len(args) >= 2: t0 = dt.datetime.strptime(args[0], "%H:%M").time(); t1 = dt.datetime.strptime(args[1], "%H:%M").time()
    rows = load(sym, day, t0, t1)
    if not rows:
        print("no samples for", sym, day, "in", DEPTH); return 1
    S = series(rows)
    runs = sorted(set(r["_path"] for r in rows))
    print("AuctionEdgeDepth overlay  %s %s  %s-%s chart-local  |  %d seconds from %d run(s)" % (sym, day, t0.strftime("%H:%M"), t1.strftime("%H:%M"), len(S), len(runs)))
    for p in runs: print("   ", p)

    # ---- health ------------------------------------------------------------------------------------------
    n = len(S); ready = sum(1 for s in S if s["ready"]); full = sum(1 for s in S if s["full"])
    print("\nHEALTH   ready %d/%d (%.0f%%)   full band %d/%d (%.0f%%)   resets %d   rejected %d   dropped %d"
          % (ready, n, 100.0 * ready / n, full, n, 100.0 * full / n, S[-1]["resets"], S[-1]["rejected"], S[-1]["dropped"]))
    print("         a lead-lag read below is only as good as the ready share; below ~50% the book is not a continuous witness")

    # ---- lead-lag --------------------------------------------------------------------------------------
    print("\nLEAD-LAG  Pearson r of the read at t with price(t+h) - price(t), ready seconds only")
    print("          %-24s %s" % ("read", "   ".join("h=%-3ds r      n" % h for h in HORIZONS)))
    reads = [("stack (near, bid-ask)", lambda s: s["stack"]),
             ("refill share bid-ask", lambda s: (s["refill_bid"] - s["refill_ask"]) if not (math.isnan(s["refill_bid"]) or math.isnan(s["refill_ask"])) else None),
             ("pulls bid-ask", lambda s: s["pull_bid"] - s["pull_ask"]),
             ("executed bid-ask (near)", lambda s: s["exec_bid"] - s["exec_ask"])]
    for step, label in ((1, "every second, overlapping windows"), (30, "every 30 s, non-overlapping")):
        print("          -- %s" % label)
        for name, value in reads:
            _, out = leadlag(S, name, value, step)
            print("          %-24s %s" % (name, "   ".join(("%+.3f %6d" % (c, k)) if not math.isnan(c) else ("   nan %6d" % k) for h, c, k in out)))
    print("          sign convention: stack > 0 and refill bid > ask are supportive; r > 0 means the read leads price up")

    # ---- pull events -------------------------------------------------------------------------------------
    print("\nPULLS     largest one-sided unmatched reductions and what price did in the next 30 s")
    for side, key, other, sign in (("offers pulled", "pull_ask", "pull_bid", +1), ("bids pulled", "pull_bid", "pull_ask", -1)):
        vals = [s[key] for s in S if s["ready"] and s[key] > 0]
        if len(vals) < 50: print("          %-14s n too small (%d ready seconds with a pull)" % (side, len(vals))); continue
        thr = pct(vals, 0.98); events = []; last = None
        for i, s in enumerate(S):
            if not s["ready"] or s[key] < thr or s[other] > 0.5 * s[key]: continue
            if last is not None and (s["t"] - last).total_seconds() < 60: continue
            fw = forward(S, i, 30)
            if fw is None: continue
            events.append(fw * sign); last = s["t"]
        if not events: print("          %-14s no isolated events" % side); continue
        through = sum(1 for e in events if e > 0)
        print("          %-14s threshold %5.0f  events %3d  price went through the pulled side %d (%.0f%%)  median %+.2f pts  mean %+.2f pts"
              % (side, thr, len(events), through, 100.0 * through / len(events), statistics.median(events), sum(events) / len(events)))

    # ---- zone visits --------------------------------------------------------------------------------------
    print("\nZONES     visits = consecutive seconds with price inside the mapped zone +/- 1 pt; defending-side refill share on the way in")
    visits = []; cur = None
    for i, s in enumerate(S):
        inzone = s["zone_low"] > 0 and s["zone_low"] - 1 <= s["price"] <= s["zone_high"] + 1
        if inzone and cur is None: cur = {"i0": i, "low": s["zone_low"], "high": s["zone_high"], "pmin": s["price"], "pmax": s["price"]}
        if inzone: cur["pmin"] = min(cur["pmin"], s["price"]); cur["pmax"] = max(cur["pmax"], s["price"]); cur["i1"] = i
        if not inzone and cur is not None:
            if cur["i1"] - cur["i0"] >= 5: visits.append(cur)
            cur = None
    if cur is not None and cur.get("i1", cur["i0"]) - cur["i0"] >= 5: visits.append(cur)
    scored = []
    for v in visits:
        mid = (v["low"] + v["high"]) / 2
        support = (v["pmin"] - v["low"]) < (v["high"] - v["pmax"])          # which edge did price press
        ex = sum(S[i]["zb_ex" if support else "za_ex"] for i in range(v["i0"], v["i1"] + 1))
        rf = sum(S[i]["zb_rf" if support else "za_rf"] for i in range(v["i0"], v["i1"] + 1))
        share = rf / ex if ex > 0 else None
        fw = forward(S, v["i1"], 120)
        if share is None or fw is None: continue
        held = fw if support else -fw                                        # positive = price left the zone the defended way
        scored.append((share, held, support, S[v["i0"]]["t"], v["low"], v["high"]))
    if len(scored) < 4:
        print("          %d scoreable visits; nothing to split yet" % len(scored))
    else:
        med = statistics.median(s[0] for s in scored)
        hi = [s[1] for s in scored if s[0] >= med]; lo = [s[1] for s in scored if s[0] < med]
        print("          visits %d   refill share median %.2f" % (len(scored), med))
        print("          refill >= median: n %2d  held-way move at +2 min mean %+.2f  median %+.2f  positive %.0f%%" % (len(hi), sum(hi) / len(hi), statistics.median(hi), 100.0 * sum(1 for x in hi if x > 0) / len(hi)))
        print("          refill <  median: n %2d  held-way move at +2 min mean %+.2f  median %+.2f  positive %.0f%%" % (len(lo), sum(lo) / len(lo), statistics.median(lo), 100.0 * sum(1 for x in lo if x > 0) / len(lo)))
        for share, held, support, t, low, high in scored[-8:]:
            print("            %s  %s %.2f-%.2f  refill %.2f  +2min %+.2f" % (t.strftime("%H:%M:%S"), "support" if support else "resist ", low, high, share, held))

    # ---- minute aggregate --------------------------------------------------------------------------------
    os.makedirs(REPORTS, exist_ok=True)
    mins = defaultdict(list)
    for s in S: mins[s["t"].replace(second=0, microsecond=0)].append(s)
    mcsv = os.path.join(REPORTS, "depth_overlay_%s_%s.csv" % (day, sym))
    with open(mcsv, "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["minute_local", "last", "ready_pct", "stack_mean", "refill_bid", "refill_ask", "pull_bid", "pull_ask", "exec_bid", "exec_ask", "zone_low", "zone_high"])
        for m in sorted(mins):
            g = mins[m]; rb = [x["refill_bid"] for x in g if not math.isnan(x["refill_bid"])]; ra = [x["refill_ask"] for x in g if not math.isnan(x["refill_ask"])]
            w.writerow([m.strftime("%Y-%m-%d %H:%M"), "%.2f" % g[-1]["price"], "%.0f" % (100.0 * sum(1 for x in g if x["ready"]) / len(g)),
                        "%.0f" % (sum(x["stack"] for x in g) / len(g)), "%.2f" % (sum(rb) / len(rb)) if rb else "", "%.2f" % (sum(ra) / len(ra)) if ra else "",
                        "%.0f" % max(x["pull_bid"] for x in g), "%.0f" % max(x["pull_ask"] for x in g), "%.0f" % max(x["exec_bid"] for x in g), "%.0f" % max(x["exec_ask"] for x in g),
                        "%.2f" % g[-1]["zone_low"] if g[-1]["zone_low"] > 0 else "", "%.2f" % g[-1]["zone_high"] if g[-1]["zone_high"] > 0 else ""])
    print("\nminute aggregate:", mcsv)

    # ---- picture ---------------------------------------------------------------------------------------
    try:
        import matplotlib; matplotlib.use("Agg"); import matplotlib.pyplot as plt; import matplotlib.dates as md
    except ImportError:
        print("matplotlib unavailable; no picture"); return 0
    ts = [s["t"] for s in S]; px = [s["price"] if s["price"] > 0 else float("nan") for s in S]
    fig, ax = plt.subplots(4, 1, figsize=(16, 11), sharex=True, gridspec_kw={"height_ratios": [2.2, 1, 1, 1]})
    ax[0].plot(ts, px, color="#E8F0F7", lw=0.9); ax[0].set_ylabel("last")
    seen = set()
    for s in S:
        if s["zone_low"] > 0 and (s["zone_low"], s["zone_high"]) not in seen:
            seen.add((s["zone_low"], s["zone_high"])); ax[0].axhspan(s["zone_low"], s["zone_high"], color="#E9C46A", alpha=0.12, lw=0)
    for i, s in enumerate(S):
        if not s["ready"]: ax[0].axvspan(s["t"], s["t"] + dt.timedelta(seconds=1), color="#FF7E87", alpha=0.05, lw=0)
    ax[1].bar(ts, [s["stack"] for s in S], width=1 / 86400.0, color=["#38D8BA" if s["stack"] >= 0 else "#FF7E87" for s in S]); ax[1].axhline(0, color="#24364B"); ax[1].set_ylabel("stack near")
    ax[2].plot(ts, [s["refill_bid"] for s in S], color="#38D8BA", lw=0.8, label="bid"); ax[2].plot(ts, [s["refill_ask"] for s in S], color="#FF7E87", lw=0.8, label="ask"); ax[2].set_ylim(0, 1.5); ax[2].set_ylabel("refill share"); ax[2].legend(loc="upper left", fontsize=8)
    ax[3].plot(ts, [s["pull_bid"] for s in S], color="#38D8BA", lw=0.8, label="bid pulled"); ax[3].plot(ts, [s["pull_ask"] for s in S], color="#FF7E87", lw=0.8, label="offers pulled"); ax[3].set_ylabel("unmatched"); ax[3].legend(loc="upper left", fontsize=8)
    for a in ax: a.set_facecolor("#0B1220"); a.grid(color="#24364B", lw=0.4); a.tick_params(colors="#91A6BB"); a.yaxis.label.set_color("#91A6BB")
    ax[3].xaxis.set_major_formatter(md.DateFormatter("%H:%M")); fig.patch.set_facecolor("#0B1220")
    fig.suptitle("AuctionEdgeDepth overlay  %s %s  (red tint = book not ready)" % (sym, day), color="#E8F0F7")
    png = os.path.join(REPORTS, "depth_overlay_%s_%s_%s-%s.png" % (day, sym, t0.strftime("%H%M"), t1.strftime("%H%M")))
    fig.tight_layout(); fig.savefig(png, dpi=110, facecolor=fig.get_facecolor()); print("chart:", png)
    return 0


if __name__ == "__main__":
    sys.exit(main() or 0)
