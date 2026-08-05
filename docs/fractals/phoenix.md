# Phoenix

[![Phoenix](../gallery/phoenix.jpg)](../gallery/phoenix.jpg)

The only formula here that remembers: each step uses the previous value as well as the current one.

## The rule

`z → z² + p₁ + p₂·z_prev`, iterated from the pixel with both constants fixed. Carrying one iterate of history makes it a second-order recurrence rather than a plain function of the current point, which is where the curling tendrils come from — they are not something a first-order map produces.

## Where it came from

Shigehiro Ushiki published it as "Phoenix" in IEEE Transactions on Circuits and Systems 35(7), 788–789, in July 1988, at Kyoto University. The paper presents it as a complex one-dimensional section of a Julia-like set of a **complexified Hénon map** — the memory term is what is left of the second dimension of the Hénon map after the slice.

## Worth knowing

The Hénon map is one of the standard examples of a strange attractor in dynamical systems, so the Phoenix is a fractal that arrived from the chaos-theory side of the subject rather than from complex analysis, and reached the fractal-art world afterwards.

## How this program draws it

Drawn with the axes swapped, which is what stands the flames upright rather than laying them on their side.

It was also the one formula that came out visibly wrong on the first attempt here. Feeding the pixel in as `c`, the way a Mandelbrot does, leaves the imaginary part unseeded: the whole orbit stays on the real axis and the picture is a set of flat horizontal bands. It has to start from the pixel like a Julia set, with both constants fixed. See `Phoenix` in [Mandelbrot.cs](../../Mandelbrot.cs).

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal phoenix
```

[← All twenty-six](../../README.md#every-fractal)
