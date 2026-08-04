# Fractal Zoom

A C# / SkiaSharp app that flies into the Mandelbrot set continuously and never stops.

```bash
dotnet run -c Release
```

It opens on a settings screen over a live preview of the first view — pick how it renders, press
enter to start. `esc` brings the menu back at any time, and it has an Exit row.

Keys: `esc`/`tab` settings · `space` pause · `R` new descent · `↑`/`↓` zoom speed ·
`H` toggle readout · `Q` quit outright

```
--size WxH        window size (default 1280x800)
--speed N         zoom e-folds per second (default 0.25), held steady
--seed N          RNG seed, which decides the route it takes
--quality N       kernel resolution vs the window, 0.2-2.0 (default 1); above 1 supersamples
--no-menu         skip the startup settings screen
--duration N      exit after N seconds (default: run forever)
--snapshot FILE   write the last frame to FILE as a PNG on exit
```

## Startup settings

[SettingsScreen.cs](SettingsScreen.cs) draws the menu over the actual opening view, with the zoom
held still (a zero throttle freezes both scale and pan) and every setting applied on the frame it
changes — so the choices can be seen rather than guessed at.

| Setting | What it trades |
| --- | --- |
| Detail | Kernel pixels against the window. *Super crisp* and *Sharper* supersample; lower settings are softer but each descent reaches much deeper. |
| Zoom speed | Held constant while a descent lasts, so slower also means it gets further down. |
| Motion sharpness | How much a frame may be stretched while the next computes. |
| Colours | A fixed gradient, or a new one per descent. |
| Readout | The figures in the corner. |

**Super crisp** renders above the window resolution (2× linear, so four samples per pixel) and lets
Skia downsample with mipmaps — plain bilinear only reads a 2×2 neighbourhood and would alias most of
the extra samples away. It resolves structure finer than a pixel instead of letting the band-limited
colouring average it into a smooth wash. Measured on an identical frozen view, the Laplacian standard
deviation of a detailed patch rises from 56.4 to 62.6, and filaments visibly hold their shape instead
of breaking into speckle. Kernel buffers land in the 20-24M pixel range for a 1280×800 window on a retina
display (~730 MB peak RSS against 358 MB at Native), and a 24M-pixel ceiling keeps a larger display
from asking for gigabytes — supersampling squares with the factor, so an uncapped 2× on a 5K panel
would want well over 100M pixels per buffer.

Because supersampling multiplies kernel cost by the square of the factor, and the zoom rate is held
steady, selecting it **pulls the speed row down with it** — at the normal rate a super-crisp descent
becomes unsustainable within seconds and ends almost immediately. That happens visibly in the menu
rather than as a hidden factor, so it stays overridable. Paired: `--quality 2 --speed 0.1` sustained a
single descent to 1.3e7× over 100 s, where `--quality 2 --speed 0.25` burned through 7 descents in 75 s.

Below the settings are three action rows — *Start* / *Resume*, *Start a new descent* and *Exit* —
so the menu is also the way out. `esc` toggles: into the menu from the zoom, and back out of it.

Values passed on the command line preselect the matching entries, so the two cannot disagree, and
reopening the menu syncs its Readout row to whatever `H` last left it at. `--no-menu` skips the screen
entirely, which is what the timed and snapshot runs use.

The state machine is covered by 34 assertions (navigation wrapping, left/right being inert on action
rows, each action's return value, preselection) — worth having, since keystrokes cannot be driven
through GLFW headlessly.

## Running from an IDE

**Visual Studio** — open `FractalZoom.sln`. The Run dropdown is populated from
`Properties/launchSettings.json` with the useful argument combinations already set up (*Deep dive*,
*Max sharpness*, *Snapshot after 30s*, *Small window*). The same profiles work from the CLI:

```bash
dotnet run -c Release --launch-profile "Deep dive"
```

**VS Code** — F5. `.vscode/launch.json` has matching configurations and builds Release first, which
matters: this is a compute-bound renderer, so the configuration you launch changes how deep it gets.
There is also a *Debug build* configuration for breakpoints, in a smaller window since it is slower.

Note that `Optimize` is enabled for **both** configurations in the csproj, deliberately — an
unoptimised build of the escape-time kernel is slow enough to look broken rather than merely slower.
The trade is that stepping through the kernel sees some inlining.

## Platforms

Runs on macOS, Windows and Linux — Silk.NET/GLFW for the window and OpenGL 3.3 for the surface, all
of which are portable. Native assets for all three ship in the build, so `dotnet run` is enough:

```bash
dotnet publish -c Release -r linux-x64  --self-contained false
dotnet publish -c Release -r win-x64    --self-contained false
dotnet publish -c Release -r osx-arm64  --self-contained false
```

Two portability details that are easy to get wrong: the base `SkiaSharp` package carries no native
library at all, so `SkiaSharp.NativeAssets.Linux`, `.Win32` and `.macOS` are all referenced
explicitly (without the Linux one it builds fine and then fails at startup); and the HUD font is
resolved from a candidate list per platform, because `SKTypeface.FromFamilyName` substitutes a
default instead of returning null for a missing family, so the name has to be checked on the result.

## How deep it goes

The zoom rate is held steady, so depth is bounded by what the machine can render at that rate rather
than by precision — a descent ends and a new one begins when it can no longer keep up. Measured on an
M4 Max (16 cores), 1280×800 window on a retina display:

| settings | descent length | reached |
| --- | --- | --- |
| defaults — native detail, steady speed | 144 s | 1.5e16× |
| " (second descent, same run) | ~150 s | 1.9e19× |

Lower the detail or the speed and descents go considerably further, because depth compounds: a
cheaper kernel sustains the rate longer, and the deeper it gets the more iterations the bilinear
approximation can skip, which makes the kernel cheaper again. For reference, under the older
*variable*-rate design (which slowed to a crawl instead of ending) the same machine reached 4.5e85× in
two minutes at `--quality 0.4 --speed 2.5` — depth was available, it just did not look or feel good
getting there.

## How it works

**Window and canvas** — [Program.cs](Program.cs) opens a GLFW/OpenGL window via Silk.NET and binds
a SkiaSharp GPU surface (`GRContext.CreateGl`) to framebuffer 0. Everything drawn — the fractal, the
fade, the HUD — goes through one `SKCanvas`.

**Where to fly** — [ZoomDirector.cs](ZoomDirector.cs) shrinks the view scale exponentially, so zoom
is linear in log space and reads as constant speed. It re-aims on every 1.25× of magnification, which
is cheap — a few hundred escape-time samples against the kernel's billions of iterations — and both
tracks the boundary closely and notices a view going featureless within a second. Re-anchoring the
reference orbit is much rarer, once per doubling, since that means rebuilding a high-precision orbit
and its approximation table.

A descent also **fast-forwards through its first five levels instantly**. The wide view of the whole
set is mostly smooth exterior — a potential field with detail only along the boundary — and it reads
as blurry no matter how many pixels it is rendered at, because there genuinely is no fine structure
out there. Skipping to ~1000× means the first visible frame already sits among filaments.

**Staying on the boundary** — a point exactly on the set boundary can be zoomed into forever without
running out of detail, so the closer the target is to it, the less the camera has to steer. This
matters more than it sounds: with the target chosen as the slowest-escaping sample of a coarse grid,
its distance from the boundary is only as good as the grid spacing, and after zooming 4× that error
is four times larger relative to the view. Repeat it and the camera drifts off into featureless
exterior and spends minutes magnifying empty space. Simulating 40 descents, **every single one died
that way**, some as shallow as 4e14×.

Three changes fixed it, in order of importance:

- **Hill-climb the target onto the boundary.** Escape time grows without limit as the boundary is
  approached, so a pattern search on it with a shrinking radius converges to a point just outside —
  interior samples are rejected, which parks the target where there is both structure and colour.
- **Re-pin the existing course rather than picking a new target.** Choosing afresh on every re-aim is
  actively harmful: the camera never converges on anything, it just chases. The normal path refines
  from where the camera is already heading.
- **Give up gracefully when a region really is exhausted.** If the search finds no exterior at all,
  or its best target cannot climb to a quarter of the iteration budget, the descent cross-fades to a
  new one instead of grinding onward.

Simulated over 40 descents, that is 0 deaths below 1e60× magnification, with several reaching the
1e290 precision floor.

**The kernel** — [Mandelbrot.cs](Mandelbrot.cs) is an escape-time renderer run with `Parallel.For`
across all but one core, in two passes: an escape-time field, then colouring. Interior points are
cheap — the main cardioid and period-2 bulb are rejected algebraically, and everything else gets
periodicity detection so a cycling orbit stops early.

**Depth** — see [the next section](#going-deeper-than-double-precision). Above 1e13× magnification
the kernel switches to perturbation against a high-precision reference orbit.

**Why it isn't pixelated** — three things, all worth knowing about:

1. *The kernel runs off the display thread.* [FractalRenderer.cs](FractalRenderer.cs) renders on a
   worker while the window animates at vsync. Each display frame takes the newest finished frame and
   re-projects it onto the current camera with a Skia translate+scale derived from the two views'
   complex-plane coordinates, so every point stays geometrically aligned. The kernel is therefore
   free to take a second or more without the animation stuttering.

2. *It renders ahead of the camera, with overscan.* The worker is given the camera position the view
   will have once the kernel finishes, so the finished frame lands at ~1:1 instead of always needing
   to be magnified. A frame only covers the window if its view is at least as wide as the view at the
   moment it lands, which takes three things: the lead deliberately undershoots the latency estimate,
   the target offset is hard-capped at 40% of the view (`MaxTargetOffset`), and the kernel buffer
   covers extra field of view at matching pixel density. That overscan **scales with the drift
   budget** rather than being fixed — a flat 6% left visible gaps of ~160px at the highest zoom
   speed, because the faster the zoom the further the camera travels during a frame's life and the
   more a jittery latency estimate can miss by. Audited at 0 gaps in ~16,000 frames across three
   speeds.

3. *The palette is band-limited.* Colouring runs over the escape-time field rather than inline. For
   each pixel it estimates how many colour cycles that pixel spans from the local gradient of the
   field, and attenuates the palette's oscillating component toward its mean by `1/(1+3·dv²)`. Where
   structure is finer than a pixel the colour converges to the average instead of sampling the ramp
   at random — the same idea as analytic antialiasing in a shader. It is what turns deep filaments
   from salt-and-pepper noise into smooth structure, and it costs one extra pass rather than the 4×
   of supersampling.

## Going deeper than double precision

A `double` carries ~16 significant digits, so plain escape-time iteration stops resolving
neighbouring pixels at roughly **1e13×** magnification — about 75 seconds into a descent. The fix is
**perturbation**, in [ReferenceOrbit.cs](ReferenceOrbit.cs) and [BigFixed.cs](BigFixed.cs).

Instead of iterating each pixel's `c` in high precision, one high-precision *reference orbit* `Z` is
computed for a nearby anchor point. With `dz = z - Z` and `dc = c - C`, the iteration becomes

```
dz' = 2·Z·dz + dz² + dc
```

`dc` and `dz` stay tiny, so their *relative* double precision still separates pixels 1e-60 apart —
which is the whole point. Only the one orbit needs big arithmetic, a few thousand iterations per
rebuild rather than per pixel, so this is barely slower per pixel than the plain kernel. Details
that matter:

- **Rebasing.** Whenever the orbit passes nearer the origin than the delta itself (`|z| < |dz|`),
  `dz` is reset to `z` and the reference index to 0. Since `Z[0]` is exactly 0 this is lossless, and
  it restores precision exactly where cancellation would otherwise destroy it. This is what removes
  the "glitches" that perturbation renderers are known for, without needing a second orbit.
- **The anchor is chosen to be interior when possible.** The director already knows which of its
  grid samples never escaped; anchoring there keeps the orbit bounded for the whole iteration
  budget, so pixels never have to rebase off the end of it.
- **Orbit values are stored as `double`.** Their rounding error is common to every pixel in the
  view, so it acts as a negligible shift of the whole image rather than as per-pixel noise. Only the
  anchor coordinate needs the big fixed-point type.

This moves the wall from 1e13× to about **1e290×**, where the pixel deltas themselves stop being
representable as doubles. Verified against ground truth computed entirely in 400-bit fixed point:

| view scale | perturbed | plain fp64 |
| --- | --- | --- |
| 1e-16 | 100% | 36% |
| 1e-25 | 100% | 0% |
| 1e-40 | 100% | 0% |
| 1e-140 | 100% | 0% |

(300 samples each around a boundary point located to 1e-47, agreement meaning the smooth escape time
matches to within 0.01.)

### Skipping iterations: BLA

Perturbation makes depth *possible*; [BlaTable.cs](BlaTable.cs) makes it *fast*. While a pixel's
delta is small the `dz²` term is negligible and the step is effectively linear, `dz' = A·dz + B·dc`.
Linear maps compose, so a run of consecutive steps collapses into one `(A, B)` pair. The table holds
those pairs for runs of 2, 4, 8, … steps, each with the radius of `|dz|` inside which it stays
accurate, and a pixel takes the longest jump its delta currently allows.

Measured against the step-by-step path at the same coordinates:

| view scale | speedup | agreement |
| --- | --- | --- |
| 1e-20 | 1.6× | 99.93% (worst error 0.2 iterations) |
| 1e-40 | 3.0× | 100% |
| 1e-80 | 458× | 100% |

The gain grows with depth because a run's length is limited by how far below `Epsilon·|Z|` the delta
is, and deep views have dozens of decades of headroom. Two things are worth knowing:

- **The error tolerance is set by measurement, not theory.** The per-skip error compounds along a
  run, so loose values are visibly wrong — at `1e-6` the escape times disagreed for 12% of pixels.
  `1e-16` was the largest value with no interior/exterior flips at any depth tested, and flips are
  the visible failure mode (isolated black specks). Speed was traded for that.
- **The table is only built when it can actually be used.** A skip needs `|dc|` below `Epsilon·|Z|`,
  so above ~1e16× magnification there is nothing to gain and the table is skipped entirely; below
  that it would be built, consulted, and never hit — measurably slower than plain iteration.

### What gives instead: the descent ends

Perturbation removes the precision wall and BLA removes most of the cost wall, but not all of it: the
iteration count needed still climbs with depth — a few hundred near the top, ~3300 at 1e19×. So
something has to give. Three candidates, and the choice matters:

- *Resolution* — rejected. Sacrificing it is what made deep views look pixelated.
- *Iteration count* — rejected, and measured rather than assumed: at 1e-20 every escaping pixel
  sampled took more than 0.75× the budget, so trimming it would turn escaping pixels into wrongly
  black ones rather than save work.
- *The zoom rate* — the earlier design, but letting it fall without limit makes the descent visibly
  grind to a halt.

So the rate is held **near-constant** — free to vary only between 60% and 100% of what was asked, which
reads as steady — and when even that floor cannot hold the drift budget, the descent **cross-fades to a
fresh one** rather than crawling. Depth per descent is therefore bounded by what the machine can
render at a steady rate, not by precision.

Measured at the defaults (native detail, steady speed): a descent ran **144 s to 1.5e16×**, and the
next reached 1.89e19× — sharp throughout, at the full framebuffer resolution, with the throttle
staying inside 0.60–1.00. Lower the detail or the speed and descents go considerably further.

One subtlety worth recording: the give-up test only applies in the steady state. A new descent starts
while the latency estimate still holds the previous one's deep, slow value, and counting that ended
fresh descents 1.8 s after they began.

### The GPU, and what it would take

Skia's own work — the blit, the resampling, the fade, the HUD text — runs on the GPU through the
OpenGL backend. The Mandelbrot kernel does not; it uses all CPU cores but one, deliberately, so the
display thread keeps hitting vsync.

The GPU would be dramatically faster. Measured with a fp32 escape-time kernel written as an
`SKRuntimeEffect`, at 2560×1600 and 2000 iterations:

| | time |
| --- | --- |
| GPU, fp32 SkSL shader | **2.0 ms** |
| CPU, fp64 kernel (15 cores) | 139.7 ms |

That is ~70×, and it is real rather than a missed synchronisation — the time scales linearly with the
iteration budget (1.48 ms at 125 iterations, 14.95 ms at 2000, measured on an all-interior view where
no pixel escapes early).

What stops it being a drop-in replacement is precision, and the constraints are worth recording:

- **No fp64 in a shader.** SkSL is fp32 only. On Apple silicon there is no alternative — Metal
  Shading Language has no `double` type either. (On Windows/Linux a native GLSL compute shader
  *can* use fp64 via GL 4.0, though consumer NVIDIA runs it at a fraction of fp32 rate.) A plain
  fp32 kernel runs out of precision around 1e5× magnification, 25 decades short of the interesting
  part.
- **SkSL has no `while` loops**, and `for` loops need a compile-time bound which it unrolls: 2048
  iterations compile, 4096 fails with "program is too large". A deep view needs ~4600, so the kernel
  would have to be split across passes with state carried in a float texture.

So the viable design is not "port the kernel" but *rescaled perturbation on the GPU*: keep computing
the reference orbit on the CPU in high precision, upload it as a texture, and have the shader iterate
only the deltas in fp32 — carrying an explicit power-of-two scale factor so they stay inside fp32's
exponent range, which is the binding limit rather than its mantissa. Written in SkSL it would stay
portable across all three platforms. It is a substantial piece of work and is not implemented here;
what is implemented is the CPU path described above.

### The cross-fade

The director still cross-fades to a fresh descent with a new palette if a view ever reaches the
`1e-290` floor, and `R` triggers the same thing on demand — but at realistic throttled speeds that
floor is no longer something you reach by waiting. The `cycle` counter in the HUD tracks it.
