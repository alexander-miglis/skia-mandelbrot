# Nova

[![Nova](../gallery/nova.jpg)](../gallery/nova.jpg)

Newton's method with the pixel added back in every step, which turns a root-finder into a Mandelbrot-like parameter map.

## The rule

Take the Newton step for `z³ − 1` and add a constant: `z → z − f(z)/f'(z) + c`, started from a fixed point with `c` read from the pixel. The added term keeps knocking the iteration off its roots, so instead of three clean basins you get a Mandelbrot-like structure of regions where the perturbed method still settles and regions where it never does.

## Where it came from

**Paul Derbyshire** introduced it in the mid-1990s, as a modification of Newton's method intended to change its rate of convergence. It spread through the fractal software of that era rather than through journals — which is true of a fair number of the formulas in this program, and is why the attributions for them are harder to pin down than for the ones with a paper behind them.

## Worth knowing

It is a good illustration of how thin the line is between "numerical method" and "fractal". Nothing about a relaxed Newton iteration is meant to be decorative; the pictures are a side effect of asking where a root-finder fails, and the shapes that appear are the shapes of failure.

## How this program draws it

Shares its code with the [Newton](newton.md) fractal — the same routine with a non-zero `add` term, started from `z = 1` instead of from the pixel. See `Newton(...)` in [Mandelbrot.cs](../../Mandelbrot.cs).

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal nova
```

[← All twenty-six](../../README.md#every-fractal)
