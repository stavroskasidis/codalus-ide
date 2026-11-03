"""Summarizes the scroll A/B (scroll-ab.yml): per version, every run's frame statistics and the pooled ones, and a
two-sided Mann-Whitney U test (normal approximation, tie-corrected) of the runs' p50s, p99s and frames over 50 ms."""
import glob, json, math, os, sys

root, os_name = sys.argv[1], sys.argv[2]

def pct(xs, q):
    xs = sorted(xs)
    return xs[max(0, math.ceil(q * len(xs)) - 1)] if xs else float("nan")

def runs_of(version):
    """Each result file is one run (scroll-ab.yml runs the harness once per launch)."""
    runs, cpu = [], ""
    for path in sorted(glob.glob(os.path.join(root, version, "*", "**", "self-driven-*.json"), recursive=True)):
        d = json.load(open(path))
        frames = d["samples"].get("selfdriven_scroll_frame_ms", [])
        if frames:
            runs.append(frames)
        cpu = d["machine"].get("cpu", "").strip()
    return runs, cpu

def mann_whitney(a, b):
    combined = sorted([(x, 0) for x in a] + [(x, 1) for x in b])
    ranks = [0.0] * len(combined)
    i = 0
    ties = 0.0
    while i < len(combined):
        j = i
        while j + 1 < len(combined) and combined[j + 1][0] == combined[i][0]:
            j += 1
        r = (i + j) / 2 + 1
        for k in range(i, j + 1):
            ranks[k] = r
        t = j - i + 1
        ties += t ** 3 - t
        i = j + 1
    ra = sum(r for r, (_, g) in zip(ranks, combined) if g == 0)
    n1, n2 = len(a), len(b)
    u = ra - n1 * (n1 + 1) / 2
    n = n1 + n2
    mu = n1 * n2 / 2
    sigma = math.sqrt(n1 * n2 / 12 * ((n + 1) - ties / (n * (n - 1)))) if n > 1 else 0
    if sigma == 0:
        return u, 1.0
    z = (abs(u - mu) - 0.5) / sigma
    return u, math.erfc(z / math.sqrt(2))

data = {v: runs_of(v) for v in ("A", "B")}
print(f"## Scroll A/B on {os_name}\n")
print(f"CPU: {data['A'][1] or data['B'][1]}. A = develop before the fixes, B = the fixes; ABBA ABBA, 5 runs a round.\n")
print("| version | runs | frames/run (median) | frame p50 (pooled) | p99 (pooled) | max | over 50 ms/run (median) | run p50s | run p99s |")
print("|---|---|---|---|---|---|---|---|---|")
stats = {}
for v in ("A", "B"):
    runs = data[v][0]
    pooled = [x for r in runs for x in r]
    p50s = [pct(r, 0.5) for r in runs if r]
    p99s = [pct(r, 0.99) for r in runs if r]
    over = [sum(1 for x in r if x > 50) for r in runs]
    counts = [len(r) for r in runs]
    stats[v] = (p50s, p99s, over, counts)
    if not runs:
        print(f"| {v} | 0 | | | | | | | |")
        continue
    print(f"| {v} | {len(runs)} | {pct(counts, 0.5)} | {pct(pooled, 0.5):.1f} | {pct(pooled, 0.99):.1f} | {max(pooled):.1f} | {pct(over, 0.5)} | "
          f"{min(p50s):.1f}-{max(p50s):.1f} | {min(p99s):.1f}-{max(p99s):.1f} |")
print()
if all(stats[v][0] for v in ("A", "B")):
    for name, index in (("run p50", 0), ("run p99", 1), ("frames over 50 ms per run", 2), ("frames per run", 3)):
        u, p = mann_whitney(stats["A"][index], stats["B"][index])
        print(f"- {name}: Mann-Whitney U = {u:.0f}, p = {p:.2g}")
