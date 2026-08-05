# Pickover Stalks

[![Pickover Stalks](../gallery/pickover-stalks.jpg)](../gallery/pickover-stalks.jpg)

The same iteration as a Julia set, but what is recorded is not when the orbit escaped — it is how close it ever came to the axes.

## The rule

Iterate as usual, but at every step measure the distance from the orbit to the real and imaginary axes and remember the smallest one seen. Colour by that. Points whose orbits happen to graze an axis come out bright, and because "grazing an axis" is a much finer condition than "escaped by step n", the result is a network of thin stalks running through regions that escape-time colouring shows as smooth.

## Where it came from

**Clifford Pickover** devised the method at IBM's Thomas J. Watson Research Center in the 1980s. His version was the "epsilon cross" — a cross-shaped trap sitting on the axes — and the structures it revealed were named after him. The same work produced his **biomorphs**, which come from a small change to the convergence test and look startlingly like microscope slides of invertebrates.

## Worth knowing

This is the ancestor of every **orbit trap** technique: once you notice that you may colour a pixel by anything the orbit does rather than only by when it left, the trap can be any shape at all — a point, a circle, a line, an image. See [Orbit Trap](orbit-trap.md) for the circle version, which is the same idea with a different target.

## How this program draws it

Traps are cheap and settle early, so the iteration is capped at 400 steps rather than the full budget — the closest approach has almost always happened long before an escape count would. The trap distance is turned into a palette coordinate by a logarithm, so an orbit that comes ten times closer moves a fixed distance along the gradient.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal pickover
```

[← All twenty-six](../../README.md#every-fractal)
