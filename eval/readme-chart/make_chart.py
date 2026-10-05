"""Draws docs/images/watts-chart.svg from one round of a watts-probe report.

    python3 make_chart.py ../model-vs-measured/m2-air-2026-10-04.txt 1 ../../docs/images/watts-chart.svg

The arguments are the report, the round to draw (1 = the first), and the output. The SVG follows
the viewer's light or dark theme, and draws its line in once when shown (still, for readers who
ask for reduced motion).
"""
import re
import sys

report, wanted, out = sys.argv[1], int(sys.argv[2]), sys.argv[3]
xs, ys, phase_of = [], [], []
machine = ""
source = ""
round_no = 0
for line in open(report, encoding="utf-8"):
    if line.startswith("Hardware:"):
        machine = re.search(r'CPU "([^"]+)"', line).group(1)
    if line.startswith("== Round"):
        round_no += 1
    m = re.match(r"\s*(\d+)\s+(idle2|idle|load)\s+([\d.]+)\s+(\w+)\s+[\d.]+\s+[\d.]+\s+(.*)$", line)
    if m and round_no == wanted:
        xs.append(float(m.group(1)))
        ys.append(float(m.group(3)))
        phase_of.append(m.group(2))
        source = source or m.group(5).split(":")[0]
xs = [x - xs[0] for x in xs]
load = (xs[phase_of.index("load")], xs[phase_of.index("idle2")])
rows = [{"source": source}]

W, H = 760, 316
left, right, top, bottom = 48, 92, 72, 40
pw, ph = W - left - right, H - top - bottom
xmax = xs[-1]
ymax = max(10, (int(max(ys) / 10) + 1) * 10)
X = lambda v: left + v / xmax * pw
Y = lambda v: top + ph - v / ymax * ph

points = " ".join(f"{X(x):.1f},{Y(y):.1f}" for x, y in zip(xs, ys))
length = sum(((X(xs[i]) - X(xs[i - 1])) ** 2 + (Y(ys[i]) - Y(ys[i - 1])) ** 2) ** 0.5 for i in range(1, len(xs)))
grid = "".join(
    f'<line class="grid" x1="{left}" x2="{left + pw}" y1="{Y(v):.1f}" y2="{Y(v):.1f}"/>'
    f'<text class="tick" x="{left - 8}" y="{Y(v) + 4:.1f}" text-anchor="end">{v}</text>'
    for v in range(0, ymax + 1, 10))
xticks = "".join(
    f'<text class="tick" x="{X(v):.1f}" y="{top + ph + 20}" text-anchor="middle">{v} s</text>'
    for v in range(0, int(xmax) + 1, 15))
last = ys[-1]
label = rows[0]["source"].split(":")[0]

svg = f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" width="{W}" height="{H}" role="img" aria-labelledby="title desc">
<title id="title">Wall watts on a MacBook Air ({machine}), measured, through idle, every core busy, and idle again</title>
<desc id="desc">Recorded once a second by watts-probe. Idle about {sum(ys[:10]) / 10:.0f} W; under load up to {max(ys):.0f} W.</desc>
<style>
  svg {{ --surface: #fcfcfb; --ink: #0b0b0b; --ink-2: #52514e; --grid: #e6e5e0; --band: #f1f0ec; --series: #2a78d6; }}
  @media (prefers-color-scheme: dark) {{
    svg {{ --surface: #0d1117; --ink: #f0f6fc; --ink-2: #9198a1; --grid: #262c36; --band: #161b22; --series: #3987e5; }}
  }}
  .bg {{ fill: var(--surface); }}
  .band {{ fill: var(--band); }}
  .grid {{ stroke: var(--grid); stroke-width: 1; }}
  .tick {{ fill: var(--ink-2); font: 12px -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif; }}
  .title {{ fill: var(--ink); font: 600 15px -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif; }}
  .sub {{ fill: var(--ink-2); font: 12px -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif; }}
  .value {{ fill: var(--ink); font: 600 13px -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif; }}
  .line {{ fill: none; stroke: var(--series); stroke-width: 2; stroke-linejoin: round; stroke-linecap: round;
           stroke-dasharray: {length:.0f}; stroke-dashoffset: {length:.0f}; animation: draw 4s ease-out 0.3s forwards; }}
  .dot {{ fill: var(--series); stroke: var(--surface); stroke-width: 2; opacity: 0; animation: show 0.3s ease-out 4.2s forwards; }}
  .end {{ opacity: 0; animation: show 0.3s ease-out 4.2s forwards; }}
  @keyframes draw {{ to {{ stroke-dashoffset: 0; }} }}
  @keyframes show {{ to {{ opacity: 1; }} }}
  @media (prefers-reduced-motion: reduce) {{ .line {{ animation: none; stroke-dashoffset: 0; }} .dot, .end {{ animation: none; opacity: 1; }} }}
</style>
<rect class="bg" width="{W}" height="{H}" rx="8"/>
<text class="title" x="{left}" y="24">Wall watts, measured once a second</text>
<text class="sub" x="{left}" y="42">MacBook Air ({machine}) on battery · {label} · from a watts-probe field report</text>
<rect class="band" x="{X(load[0]):.1f}" y="{top}" width="{X(load[1]) - X(load[0]):.1f}" height="{ph}"/>
<text class="sub" x="{(X(load[0]) + X(load[1])) / 2:.1f}" y="{top + 16}" text-anchor="middle">every core busy</text>
<text class="sub" x="{(left + X(load[0])) / 2:.1f}" y="{top + 16}" text-anchor="middle">idle</text>
<text class="sub" x="{(X(load[1]) + left + pw) / 2:.1f}" y="{top + 16}" text-anchor="middle">idle</text>
{grid}{xticks}
<text class="tick" x="{left - 8}" y="{top - 10}" text-anchor="end">W</text>
<polyline class="line" points="{points}"/>
<circle class="dot" cx="{X(xs[-1]):.1f}" cy="{Y(last):.1f}" r="4"/>
<text class="value end" x="{X(xs[-1]) + 10:.1f}" y="{Y(last) + 4:.1f}">{last:.1f} W</text>
</svg>
"""
open(out, "w", encoding="utf-8").write(svg)
print(f"{out}: {len(ys)} samples, {xmax:.0f} s, load {load[0]:.0f}-{load[1]:.0f} s, max {max(ys):.1f} W")
