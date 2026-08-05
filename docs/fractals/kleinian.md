# Kleinian

[![Kleinian](../gallery/kleinian.jpg)](../gallery/kleinian.jpg)

The limit set of a group of Möbius transformations: not an iterated point, but the set that a whole group of symmetries piles up on.

## The rule

Take a small collection of Möbius transformations — the maps `z → (az+b)/(cz+d)` that send circles to circles — and consider every word you can spell in them and their inverses. Applied to a starting point, almost all of that infinite orbit accumulates on a set of measure zero: the **limit set**. That is the fractal. This program renders a pseudo-Kleinian distance estimator, the three-dimensional analogue.

## Where it came from

**Felix Klein** glimpsed these in the 1880s, and had almost no way to see them; the hand-drawn figures in the literature of the period are a handful of circles standing in for something infinite. The modern account is **David Mumford, Caroline Series and David Wright's** *Indra's Pearls: The Vision of Felix Klein* (2002) — named for Indra's net from the Flower Garland Sutra, an infinite array of pearls each reflecting all the others — which is unusual among mathematics books in giving you the algorithms to draw the pictures yourself.

## Worth knowing

Kleinian groups are where the [Mandelbrot set](mandelbrot.md) came from. Robert Brooks and J. Peter Matelski were studying them in 1978 when they plotted, as an aside, the first picture anyone had made of the Mandelbrot set. The two ends of this gallery are the same paper.

## How this program draws it

Ray-marched on the card with a **distance estimator**: rather than testing points for membership, the shader asks "how far can I safely step without hitting anything?" and takes that step, over and over, until the distance falls under a threshold. The surface normal — and so the shading — comes from the gradient of the same estimate. See the march shader in [GpuKernel.cs](../../GpuKernel.cs).

These six read the flat controls as a camera: the pan offsets become yaw and pitch, and the zoom becomes camera distance, so dragging and scrolling orbit the object. They need the card — there is no CPU counterpart — and they are the one group in this program where `--center 0,0` is a bad view rather than a neutral one.

It is also the fractal that made the case for that last sentence. At the default angle of `0,0` this renders **very nearly black**, which is what the first pass of this gallery shipped; with the camera anywhere sensible it is one of the better pictures in the set.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal kleinian --center 0.6,0.4 --zoom 1
```

[← All twenty-six](../../README.md#every-fractal)
