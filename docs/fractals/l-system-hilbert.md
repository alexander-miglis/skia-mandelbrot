# L-System (Hilbert Curve)

[![L-System (Hilbert Curve)](../gallery/l-system-hilbert.jpg)](../gallery/l-system-hilbert.jpg)

A single line that visits every point of a square, without crossing itself.

## The rule

Divide the square into four; visit the quadrants in an order that lets you enter one and leave the next; recurse, rotating and reflecting each sub-square so the ends line up. The limit is a continuous map from the interval onto the whole square — a curve, in the sense of being the image of a line, that has area.

## Where it came from

**David Hilbert** published it in 1891, a year after **Giuseppe Peano** produced the first space-filling curve. Peano proved it could be done; Hilbert gave the version with a picture and a clear geometric recursion, which is why this is the one everybody draws.

## Worth knowing

It is genuinely useful, which is unusual in this list. The Hilbert curve preserves locality — points close on the curve are close in the square — so it is used to linearise multi-dimensional data for database indexes and caches, and to order pixels for dithering. A one-dimensional address that remembers two-dimensional neighbourhood is worth a great deal.

## How this program draws it

Unlike every other rule here this one has to be walked **in order**, because it is a single curve and the order of the leaves *is* the curve — so it stays a depth-first recursion, with culled subtrees breaking the path rather than being drawn across.

Its depth is chosen from the visible cell count as well as the cell size. From size alone, a window wider than the curve's square asks for more cells than can be drawn, and running out partway through an ordered walk leaves a contiguous first stretch drawn and the rest missing — which reads as a solid block with a bite out of it, not as a curve. See [DrawnFractals.cs](../../DrawnFractals.cs).

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal l-system
```

[← All twenty-six](../../README.md#every-fractal)
