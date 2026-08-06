# Orbit Trap

[![Orbit Trap](../gallery/orbit-trap.jpg)](../gallery/orbit-trap.jpg)

The Mandelbrot iteration, coloured by how near the orbit ever came to a circle.

## The rule

The Mandelbrot iteration $z_{n+1} = z_n^2 + c$, with the orbit's closest approach to a **circle**
recorded instead of its escape time:

$$\tau = \min_{n \le N} \Bigl|\,|z_n| - \tfrac12\,\Bigr|$$

Colour by $\tau$. Everything about the dynamics is unchanged; only the question asked of it is
different.

### The trap can be any shape

Once you notice that a pixel may be coloured by *anything* the orbit does, the trap becomes a free
choice: a point, a line, a pair of lines ([Pickover stalks](pickover-stalks.md)), a circle, a ring of
circles, or an image — sample the picture at $z_n$ and keep the nearest hit, and the fractal comes out
wearing it. The formula is a supply of infinitely detailed paths, and the trap decides what to make
visible in them.

This is the sharpest demonstration in this program that a fractal's *picture* is a choice rather than a
fact. The Mandelbrot set, its Pickover stalks and this page are the same orbits over the same plane,
measured three ways, and they look nothing alike.

### Why a circle gives rings

The trap is at radius $\tfrac12$, inside the escape radius, so orbits pass across it rather than through
it once on the way out. Points whose orbits happen to linger near that radius — which is a condition on
the *whole* orbit, not on its ending — form a set with structure at every scale, and the near-misses
grade smoothly between them. The result reads as concentric shells because $\tau$ depends on $|z_n|$
only, so the trap has the symmetry the picture inherits.

## Where it came from

The technique generalises **Clifford Pickover's** epsilon-cross work of the 1980s (see [Pickover Stalks](pickover-stalks.md)). Once the trap can be any shape, this becomes less a fractal than a rendering method: the same iteration with a different question asked of it.

## Worth knowing

It is the clearest demonstration in this program that the *picture* of a fractal is a choice rather than a fact. The Mandelbrot set, its Pickover stalks and this are the same orbits and the same plane, coloured by three different measurements, and they look nothing alike.

## How this program draws it

Shares its implementation with [Pickover Stalks](pickover-stalks.md) — one routine, a flag choosing between the axes and the circle, and a different starting point. See `Trap(...)` in [Mandelbrot.cs](../../Mandelbrot.cs).

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal orbit
```

[← All twenty-six](../../README.md#every-fractal)
