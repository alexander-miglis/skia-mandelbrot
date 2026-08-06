# Koch Snowflake

[![Koch Snowflake](../gallery/koch-snowflake.jpg)](../gallery/koch-snowflake.jpg)

Replace the middle third of every line with two sides of a triangle, and keep going. Infinite perimeter, finite area.

## The rule

Take a segment and replace it with four segments, each a third as long: the first third, then two sides
of an equilateral triangle standing on the middle third, then the last third. Do that to every segment
of the result, forever.

As transformations of the plane it is four similarities of ratio $\tfrac13$ — no shear, no distortion,
only shrink-and-turn:

$$f_1 = \tfrac13\,R_{0^\circ}, \quad f_2 = \tfrac13\,R_{60^\circ}, \quad
f_3 = \tfrac13\,R_{-60^\circ}, \quad f_4 = \tfrac13\,R_{0^\circ}$$

each followed by the shift that puts it at its place along the segment. The curve is the set left
unchanged by applying all four and taking the union, exactly as for [the fern](barnsley-fern.md) —
same theorem, simpler maps.

### The dimension

When a set is $N$ copies of itself at ratio $r$, its dimension is whatever makes the count and the
scaling agree, $N r^D = 1$:

$$D = \frac{\log N}{\log (1/r)} = \frac{\log 4}{\log 3} \approx 1.2619$$

Not an integer, which is the whole point of the word *fractal*. Measure the curve with rulers of length
$3^{-n}$ and you need $4^n$ of them; halving the ruler more than doubles the count, so the "length"
depends on the ruler and has no limit — while the *area* is zero, since the curve is nowhere thick.
It is more than a line and less than a surface.

### Infinite perimeter, finite area

Each step multiplies the number of segments by 4 and divides their length by 3, so the perimeter after
$n$ steps is $\left(\tfrac43\right)^n$ times the original and grows without bound. The area does not.
Step $n$ adds $3\cdot 4^{\,n-1}$ new triangles, each with $\tfrac19$ the area of the ones added before,
so the total added is the geometric series

$$\frac{3}{9}\left(1 + \frac49 + \left(\frac49\right)^2 + \cdots\right) = \frac{3}{9}\cdot\frac{1}{1-4/9}
= \frac{3}{5}$$

of the original triangle's area. The snowflake settles at exactly $\tfrac85$ of the triangle you
started with, bounded for ever by a curve of infinite length.

### No tangent anywhere

At every stage the curve is made of straight pieces, and the limit is continuous: step $n$ moves any
point by at most $3^{-n}$ times a constant, so the sequence of curves is uniformly Cauchy and converges
to a continuous one. But take any point and any scale, and within that scale the curve still turns
through $60°$ — the corners never smooth out, because the construction puts fresh ones at every level.
So the direction never settles, no tangent exists, and the curve is differentiable nowhere. That is the
property von Koch built it to demonstrate.

## Where it came from

Helge von Koch presented it to the Royal Swedish Academy of Sciences on 1 March 1904, under the title *Sur une courbe continue sans tangente, obtenue par une construction géométrique élémentaire* — "on a continuous curve without tangents, obtained by an elementary geometric construction".

## Worth knowing

The title is the whole point, and it is an argument with someone. Karl Weierstrass had shown in 1872 that a function could be continuous everywhere and differentiable nowhere, which was scandalous, but his example was an infinite trigonometric series. Von Koch said plainly that this was **not satisfactory from the geometrical point of view**, because the analytic expression hides the geometric nature of the curve. So he built one you could draw. A century later that instinct — that a picture of the object is worth more than a formula for it — is the whole of fractal geometry.

## How this program draws it

Subdivided level by level rather than depth first, and stopped where a segment falls under two pixels, so what you see is always the finest version that fits the window. Coordinates are [Dd](../../Dd.cs) rather than doubles, which is what lets it keep subdividing past ~1e13×: each level adds an offset a third the size of the last, and once that offset falls below the last bit of the coordinate a double simply loses it and every piece lands in the same place. Verified at **1e22×**, depth 51. See [DrawnFractals.cs](../../DrawnFractals.cs).

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal koch
```

[← All twenty-six](../../README.md#every-fractal)
