# Burning Ship

[![Burning Ship](../gallery/burning-ship.jpg)](../gallery/burning-ship.jpg)

The Mandelbrot iteration with absolute values taken before squaring, which breaks its symmetry and produces something that looks like a ship on fire.

## The rule

`z → (|Re z| + i·|Im z|)² + c`. The absolute values make the map non-analytic — it is not a function of `z` in the complex-differentiable sense — and almost none of the Mandelbrot's theory survives that. What survives is the picture, which has hulls, masts and a rigging of antennae.

## Where it came from

Michael Michelitsch and Otto E. Rössler described it in 1992 at the Institute for Physical and Theoretical Chemistry in Tübingen, in *The "Burning Ship" and Its Quasi-Julia Sets* (Computers & Graphics 16(4), 435–438). They were exploring what happens to quadratic iteration when you insert absolute values and lose analyticity. The name is literal: the picture looks like a ship going up in flames.

## Worth knowing

The famous view is not the whole set but a detail near `-1.75 + 0.03i`, which is where the ship itself is — the gallery shot above is that one rather than the opening view. The set is symmetric about the real axis only; the absolute value on the imaginary part destroys the conjugate symmetry that makes the Mandelbrot mirror-image.

## How this program draws it

Drawn with the imaginary axis inverted, which is the orientation it is always published in — hull down, masts up. Only the formula's reading of the coordinate is flipped, so the mapping from pixels to the plane is untouched and zooming at the pointer still lands where you point.

It has no perturbed form here, so below 1e-12 it switches to the 32-digit arithmetic in [Dd.cs](../../Dd.cs) and reaches about **1e25×**. Measured on 64 neighbouring pixels at a scale of 1e-20: the double kernel returns one distinct value, the wide kernel returns 42.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal burning --center -1.75,0.03 --zoom 12
```

[← All twenty-six](../../README.md#every-fractal)
