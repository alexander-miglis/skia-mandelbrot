# Multibrot

[![Multibrot](../gallery/multibrot.jpg)](../gallery/multibrot.jpg)

The Mandelbrot with the exponent turned up: `z → z³ + c` here, and the set grows an extra lobe.

## The rule

`z → z^d + c` for `d` other than 2. A degree-`d` multibrot has `d − 1`-fold rotational symmetry, so the cubic set is two-lobed and symmetric about both axes where the quadratic is one-lobed and symmetric about one. As `d` grows the set tends toward a disc with an increasingly frilled edge.

## Where it came from

There is no single discoverer to name. The generalisation is the obvious next question once you have the quadratic case, and it was explored from the earliest days of fractal imaging in the 1980s; the name "multibrot" belongs to that popular literature rather than to a paper.

## Worth knowing

The smooth escape count is where the exponent shows up in the *code* rather than the mathematics. The usual formula `n + 1 − log₂(log|z|)` has a base-2 logarithm in it because the orbit squares each step; at degree three it has to become a base-3 one, or the bands come out unevenly spaced. Every degree needs its own constant.

## How this program draws it

Iterated as `z(r² − 3i²) + cr`, which is the cubic written out to avoid a general power function, and the smooth count divides by `log 3` instead of taking `log₂`. Below the double floor it uses the wide arithmetic in [Dd.cs](../../Dd.cs) — measured at 16 distinct values across 64 pixels at a scale of 1e-20, where plain doubles give one.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal multibrot
```

[← All twenty-six](../../README.md#every-fractal)
