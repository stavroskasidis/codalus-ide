"""Summarizes render-exp.yml: per configuration, the scroll's frames and the typing latency, and Mann-Whitney U of each
configuration's run p50s and p99s against the default's."""
import glob, json, math, os, sys
root = sys.argv[1]
def pct(xs, q):
    xs = sorted(xs); return xs[max(0, math.ceil(q * len(xs)) - 1)] if xs else float("nan")
def mw(a, b):
    c = sorted([(x, 0) for x in a] + [(x, 1) for x in b]); ranks = [0.0] * len(c); i = 0; ties = 0.0
    while i < len(c):
        j = i
        while j + 1 < len(c) and c[j + 1][0] == c[i][0]: j += 1
        for k in range(i, j + 1): ranks[k] = (i + j) / 2 + 1
        t = j - i + 1; ties += t ** 3 - t; i = j + 1
    n1, n2 = len(a), len(b); n = n1 + n2
    u = sum(r for r, (_, g) in zip(ranks, c) if g == 0) - n1 * (n1 + 1) / 2
    s = math.sqrt(n1 * n2 / 12 * ((n + 1) - ties / (n * (n - 1))))
    return 1.0 if s == 0 else math.erfc((abs(u - n1 * n2 / 2) - 0.5) / s / math.sqrt(2))
configs = sorted(os.listdir(root))
data = {}
for cfg in configs:
    runs = []
    for p in glob.glob(os.path.join(root, cfg, "*", "**", "self-driven-*.json"), recursive=True):
        d = json.load(open(p)); f = d["samples"].get("selfdriven_scroll_frame_ms", []); t = d["samples"].get("selfdriven_typing_ms", [])
        if f: runs.append((f, t, d["machine"].get("cpu", "").strip()))
    data[cfg] = runs
print("## Windows rendering paths without a GPU\n")
print("| path | runs | frames/run | frame p50 | p99 | over 50 ms/run | typing p50 | typing p99 | p (run p50 vs default) | p (run p99 vs default) |")
print("|---|---|---|---|---|---|---|---|---|---|")
base = data.get("default", [])
for cfg in configs:
    runs = data[cfg]
    if not runs: print(f"| {cfg} | 0 |"); continue
    frames = [x for r in runs for x in r[0]]; typing = [x for r in runs for x in r[1]]
    p50s = [pct(r[0], .5) for r in runs]; p99s = [pct(r[0], .99) for r in runs]
    pa = mw([pct(r[0], .5) for r in base], p50s) if base and cfg != "default" else float("nan")
    pb = mw([pct(r[0], .99) for r in base], p99s) if base and cfg != "default" else float("nan")
    print(f"| {cfg} | {len(runs)} | {pct([len(r[0]) for r in runs], .5)} | {pct(frames, .5):.1f} | {pct(frames, .99):.1f} | {pct([sum(1 for x in r[0] if x > 50) for r in runs], .5)} | "
          f"{pct(typing, .5):.1f} | {pct(typing, .99):.1f} | {pa:.2g} | {pb:.2g} |")
print(f"\nCPU: {base[0][2] if base else ''}")
