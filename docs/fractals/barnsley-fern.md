# Barnsley Fern

[![Barnsley Fern](../gallery/barnsley-fern.jpg)](../gallery/barnsley-fern.jpg)

Four affine maps, applied at random, that draw a fern. The picture is the attractor and nothing else is stored.

## The rule

Four transformations, each a 2×2 matrix and a shift, chosen at random with fixed probabilities. One flattens the whole fern onto a vertical segment — that is the stem. Two make the lower fronds. The fourth, which gets 85% of the throws, shrinks and slightly rotates the fern onto itself, and stacks the whole thing up its own stalk. Start anywhere, iterate, and plot where you land: after a few points you are on the attractor and everything you draw from then on is fern.

## Where it came from

**Michael Barnsley** set it out in *Fractals Everywhere* (1988), as the showpiece for iterated function systems and the **chaos game**. His larger claim was the collage theorem: if you can cover a shape with shrunken copies of itself, those copies' coefficients are a description of it — which made a genuine attempt at fractal image compression, twenty-four numbers in place of a photograph of a fern.

## Worth knowing

The probabilities are not the shape — they are the *sampling*. The fern is the same set whatever weights you use; the weights only decide how the random walk spends its time, which is why the stem gets 1% and still comes out solid, being so much smaller than the rest.

## How this program draws it

Two renderers, chosen by measurement. Throwing points is the classic method and much the cheapest — one multiply-add each, no recursion, and the density does the shading — but it cannot be zoomed, since a random point lands in a window a billionth of the fern across about a billionth of the time. So it runs first, and if too few points landed on screen the fern is walked as **nested boxes** instead, which covers whatever part of it is visible at any magnification.

That walk had a bug worth recording, because it is easy to write and looks nearly right: it applied each new map on the *outside*, which is the order the iteration itself runs in — but `m(parent)` is not inside `parent`, so a node does not contain its descendants and culling one throws away pieces that are on screen. A deep view of the fern's tip drew a single point.

It is also the one fractal here coloured like the thing it depicts rather than from the shared gradient, and which part is stem falls out of the rule: a point is on a stem if its recent history includes the flattening map, and how recently says which stem. See [DrawnFractals.cs](../../DrawnFractals.cs).

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal barnsley
```

[← All twenty-six](../../README.md#every-fractal)
