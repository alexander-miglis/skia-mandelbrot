# Mandelbrot

[![Mandelbrot](../gallery/mandelbrot.jpg)](../gallery/mandelbrot.jpg)

The one everything else here is a variation on: `z → z² + c`, started from zero, coloured by how long the point takes to escape.

## The rule

For each point $c$ of the plane, start at $z_0 = 0$ and iterate

$$z_{n+1} = z_n^2 + c$$

The Mandelbrot set $M$ is the set of $c$ for which the orbit stays bounded for ever.

### Why a radius of 2 settles it

You never have to iterate for ever. If $|z_n| > 2$ and $|z_n| \ge |c|$ then

$$|z_{n+1}| = |z_n^2 + c| \ge |z_n|^2 - |c| \ge |z_n|^2 - |z_n| = |z_n|\left(|z_n| - 1\right) > |z_n|$$

and the factor $(|z_n| - 1)$ exceeds 1, so each step multiplies the modulus by more than a fixed amount
and the orbit runs away. Escape is therefore *decidable*: once past 2, gone. Only membership is
undecidable in finite time, which is why the interior is drawn by giving up after an iteration budget
and colouring what is left black.

### Where the smooth colour comes from

Counting iterations gives integers, and integers give visible bands. Past the bailout radius the $+c$
matters less and less and the orbit is essentially $z \mapsto z^2$, which doubles $\log|z|$ each step,
so $\log_2 \log |z_n|$ increases by almost exactly 1 per iteration. Subtracting it from the count
therefore interpolates *between* iterations:

$$\nu = n + 1 - \log_2 \log |z_n|$$

This is the **potential**, and it is smooth across the whole exterior — which is what lets the colouring
in this program band-limit itself, because a quantity with a well-defined gradient can be low-pass
filtered and a step count cannot.

### The shape of the interior

The big cardioid is exactly the $c$ for which the iteration has an attracting fixed point. Solve
$z^2 + c = z$ with $|2z| < 1$ and you get the parametrisation

$$c = \frac{\mu}{2} - \frac{\mu^2}{4}, \qquad |\mu| < 1$$

The period-2 bulb is the disc $|c + 1| < \tfrac14$, by the same argument one level down. Both are
closed-form, and this program tests them before iterating at all — the two cheapest early-outs in the
file, and available to no other formula here.

### What is known and what is not

Douady and Hubbard proved $M$ is **connected**, by showing the complement is conformally the outside of
a disc: there is an analytic map from $\{|w|>1\}$ onto the exterior, so the exterior is simply connected
and the set cannot fall into pieces. Every apparently detached island is joined by a filament too thin
to draw. Shishikura proved the **boundary has Hausdorff dimension exactly 2** — as complicated as a
plane curve is allowed to be. Whether $M$ is *locally* connected is the MLC conjecture, still open, and a
great deal of what is believed about the set is believed conditionally on it.

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
