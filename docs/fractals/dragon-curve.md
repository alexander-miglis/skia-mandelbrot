# Dragon Curve

[![Dragon Curve](../gallery/dragon-curve.jpg)](../gallery/dragon-curve.jpg)

Fold a strip of paper in half over and over, then open it out to right angles. That is the whole construction.

## The rule

Every segment is replaced by two, meeting at a right angle, with the turn alternating side. Equivalently: take a long strip of paper, fold it in half in the same direction `n` times, unfold it so every crease is a right angle, and look at it edge on. The curve never crosses itself, and copies of it tile the plane exactly.

## Where it came from

Three NASA physicists — **John Heighway**, **Bruce Banks** and **William Harter** — investigated it in 1966; Heighway found it and Harter named it. **Martin Gardner** put it in his Mathematical Games column in *Scientific American* in 1967, which is how most people met it.

## Worth knowing

It is the fractal in **Jurassic Park**. Michael Crichton used successive iterations as the chapter headings, as the book's structure progressively unravels. Donald Knuth records that Harter pointed out to him that the dragons printed in the novel are upside down — "dead dragons".

The paper-folding description is not an analogy. If you actually fold a strip and unfold it, the sequence of left and right creases you get is exactly the sequence this rule generates, which is why it is sometimes called the paperfolding sequence.

## How this program draws it

The frontier of segments at each level comes out in the curve's own order, so a whole level goes into one Skia path as a single polyline instead of a move-and-line per segment — half the points, and the stroke joins at the corners. Its segments shrink by only 1/√2 a level, so reaching the deepest view the arithmetic supports takes upwards of 160 of them, which is why the recursion cap is 400 rather than something tidier. See [DrawnFractals.cs](../../DrawnFractals.cs).

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal dragon
```

[← All twenty-six](../../README.md#every-fractal)
