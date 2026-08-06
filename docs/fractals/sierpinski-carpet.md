# Sierpiński Carpet

[![Sierpiński Carpet](../gallery/sierpinski-carpet.jpg)](../gallery/sierpinski-carpet.jpg)

The same idea in base three: cut the middle ninth out of a square, forever.

## The rule

The same construction one base up. Divide a square into nine, discard the middle one, repeat on the
eight that remain. Eight copies at ratio $\tfrac13$:

$$D = \frac{\log 8}{\log 3} \approx 1.8928$$

As a digit test, a point is in the carpet when its base-3 expansions never have the middle digit in the
same position for both coordinates:

$$\text{not } \bigl(x_k = 1 \ \text{and}\ y_k = 1\bigr) \ \text{for any } k$$

Multiplying both coordinates by three shifts their base-3 expansions one place, so the test is: multiply,
take the integer parts, and if both are 1 the point has fallen in the hole. The level at which that
happens is what this program colours.

### Universality

The carpet is a **universal plane curve**: every compact one-dimensional subset of the plane, however
tangled, is homeomorphic to a subset of it. Sierpiński proved it in 1916, and it is a much stronger
statement than the picture suggests — every knot diagram, every graph drawn in the plane, every dust,
every curve you can imagine, all of them are copies of pieces of this single object. The
[Menger sponge](menger-sponge.md) is the corresponding statement for one-dimensional sets in *any*
number of dimensions.

### Depth per level

Each step consumes $\log_2 3 \approx 1.58$ bits of the coordinate rather than one, so the carpet runs
out of precision sooner than the [triangle](sierpinski-triangle.md): about 28 levels in a double, about
62 in the wide arithmetic of [Dd.cs](../../Dd.cs).

## Where it came from

**Wacław Sierpiński** described it in 1916, a year after the triangle. Karl Menger's [sponge](menger-sponge.md) is its three-dimensional counterpart, and the two are usually introduced together for that reason.

## Worth knowing

The carpet is a **universal plane curve**: every compact one-dimensional subset of the plane, no matter how tangled, can be embedded in it. That is a much stronger statement than it looks. Any curve you can draw — any knot, any web, any set of that dimension whatsoever — is a copy of some part of this one object.

## How this program draws it

A base-3 digit test per pixel, sharing its structure with the [triangle](sierpinski-triangle.md). Each step consumes log₂3 ≈ 1.58 bits rather than one, so it runs out sooner: about 28 levels in a double and about 62 in the wide arithmetic of [Dd.cs](../../Dd.cs).

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal "sierpinski c" --zoom 1.6
```

[← All twenty-six](../../README.md#every-fractal)
