# Julia

[![Julia](../gallery/julia.jpg)](../gallery/julia.jpg)

The same iteration as the Mandelbrot with the roles swapped: the constant is fixed and the *starting point* is the pixel.

## The rule

Iterate `z → z² + k` with `k` held fixed, starting from `z` = the pixel. Every choice of `k` gives a different set, and the Mandelbrot set is precisely the map of which ones are connected: pick `k` inside it and the Julia set is one piece, pick `k` outside and it shatters into dust. This program uses `k = -0.7269 + 0.1889i`, just outside the boundary, which is where the spiralling filaments come from.

## Where it came from

Gaston Julia wrote the 199-page *Mémoire sur l'itération des fonctions rationnelles* in 1918, at twenty-five, in hospital. He had been shot in the face on the Western Front in 1915 and lost his nose; he refused evacuation until the attack had been driven back, and after many failed operations wore a leather strap over the wound for the rest of his life. The memoir won the Académie des sciences' Grand Prix des Sciences Mathématiques that year. Pierre Fatou reached much of the same ground independently and at the same time.

## Worth knowing

The work was essentially forgotten for fifty years. There was nothing to see it with: these are sets defined by what an orbit does forever, and before computer graphics the only way to know one was to reason about it. Mandelbrot, who had been Julia's student at the École Polytechnique, was the one who eventually pointed a computer at it.

## How this program draws it

Perturbed like the Mandelbrot, but the reference orbit is a different object — the orbit of the *anchor point itself* under the fixed constant, rather than the orbit of zero under the anchor — and the per-pixel offset is added once at the start instead of every iteration. Verified sharp at **1e20×**.

That difference caused the one really subtle bug in this repository. Rebasing a perturbed orbit assumes the reference starts at zero, which is true for a Mandelbrot and false here; the missing `Z[0]` term put every rebased pixel out by one anchor and the picture came out as faceted terraces with speckle across them. Fixed in [ReferenceOrbit.cs](../../ReferenceOrbit.cs). Depth costs speed here: with no `dc` term there is nothing for the approximation table to multiply, so every iteration is taken individually.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal julia
```

[← All twenty-six](../../README.md#every-fractal)
