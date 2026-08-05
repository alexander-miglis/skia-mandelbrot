# Mandelbrot

[![Mandelbrot](../gallery/mandelbrot.jpg)](../gallery/mandelbrot.jpg)

The one everything else here is a variation on: `z → z² + c`, started from zero, coloured by how long the point takes to escape.

## The rule

For each point `c` of the plane, iterate `z → z² + c` from `z = 0` and ask whether the orbit stays bounded forever. The points where it does are the set; the points where it does not are coloured by how many steps they lasted, refined to a fraction by comparing the size of the escaping value against the bailout radius. That fractional part is what makes the bands continuous instead of stepped.

## Where it came from

The first picture of it was not Mandelbrot's. In 1978 Robert Brooks and J. Peter Matelski plotted a few hundred asterisks of it on a line printer, as a sideline to a paper about **Kleinian groups** — which is also [one of the fractals in this program](kleinian.md). Benoit Mandelbrot produced the first real visualisations in 1980 at IBM's Thomas J. Watson Research Center, and Adrien Douady and John H. Hubbard did the mathematics that made it a serious object, naming it after him.

## Worth knowing

Douady and Hubbard proved the set is **connected** — every one of those apparently detached islands is joined to the main body by a filament too thin to draw. Whether it is also *locally connected* is the MLC conjecture, still open, and much of what is known about the set is known only assuming it.

The boundary has Hausdorff dimension exactly 2, a result of Mitsuhiro Shishikura's — as complicated as a curve in the plane is permitted to be.

## How this program draws it

This is the only formula here with the full deep-zoom machinery. Above ~1e13× it iterates in plain fp64 on the card; below that it switches to **perturbation** against a high-precision reference orbit ([ReferenceOrbit.cs](../../ReferenceOrbit.cs), [BigFixed.cs](../../BigFixed.cs)) and reaches **1e290×**, plus a bilinear-approximation table ([BlaTable.cs](../../BlaTable.cs)) that skips whole runs of iterations at once.

It also gets two early-outs no other formula here can use — the main cardioid and the period-2 bulb are cheap to test in closed form — and periodicity detection for the rest of the interior. See [Mandelbrot.cs](../../Mandelbrot.cs).

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal mandelbrot
```

[← All twenty-six](../../README.md#every-fractal)
