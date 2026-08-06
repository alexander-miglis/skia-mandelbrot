# Sierpiński Triangle

[![Sierpiński Triangle](../gallery/sierpinski-triangle.jpg)](../gallery/sierpinski-triangle.jpg)

The classic middle-removed triangle, computed here as a digit test on the coordinate rather than drawn.

## The rule

The set can be defined three ways that turn out to agree, and this program uses the third.

**As a removal.** Take a triangle, cut out the middle quarter (the one joining the edge midpoints),
repeat on the three that remain. What is left after infinitely many cuts is the triangle.

**As three maps.** It is the attractor of

$$f_i(\mathbf{p}) = \tfrac12\mathbf{p} + \mathbf{v}_i, \qquad i = 1,2,3$$

three similarities of ratio $\tfrac12$ toward the three corners — the same Hutchinson construction as
[the fern](barnsley-fern.md), with the simplest possible maps. Three copies at ratio $\tfrac12$ gives

$$D = \frac{\log 3}{\log 2} \approx 1.585$$

**As a digit test.** Put the triangle in the unit square as the set of $(x,y)$ whose binary expansions
never have a 1 in the same position:

$$x = \sum_k \frac{x_k}{2^k}, \quad y = \sum_k \frac{y_k}{2^k}, \qquad x_k \wedge y_k = 0 \ \text{for all } k$$

Doubling a coordinate shifts its binary expansion left by one place, so repeatedly doubling both and
asking whether the leading bits are ever both 1 reads the digits off one at a time. That is what
[Mandelbrot.cs](../../Mandelbrot.cs) does, and the level at which the test first fires is what gets
coloured.

### Why the digit test is the right one here

Every other fractal on this page could be drawn as geometry. Testing per pixel instead means the cost is
fixed per pixel however deep the view, and the depth is limited only by how many bits of the coordinate
survive — one bit per level. A double runs out after about 45 levels, and the wide arithmetic in
[Dd.cs](../../Dd.cs) after about 100, which is the deepest budget of any formula in this program.

The same condition explains why the pattern turns up unbidden elsewhere: Pascal's triangle mod 2 is
exactly the statement that $\binom{n}{k}$ is odd iff the binary digits of $k$ are a subset of those of
$n$ — Kummer's theorem — which is the same "no carry" condition as above.

## Where it came from

**Wacław Sierpiński** described it in 1915. It is far older than that as a decoration: essentially the same pattern appears in **13th-century Cosmati mosaics** in the cathedral at Anagni and in church floors across central Italy — roughly seven hundred years before anyone wrote down what it was. The medieval craftsmen who laid those floors were iterating a rule to a depth of three or four because it looked right.

## Worth knowing

The triangle turns up in places that seem to have nothing to do with it: Pascal's triangle with the odd numbers shaded, the Tower of Hanoi's graph of legal positions, and Barnsley's chaos game, where plotting a random walk toward three corners produces it with probability one. It is what self-similar with ratio ½ and three copies *means*, so anything with that structure is it.

## How this program draws it

Testing per pixel rather than drawing has one real advantage: it keeps working as far down as the arithmetic does, where a drawn triangle would need ever more geometry. Each doubling consumes one bit of the coordinate, so a double runs out after about 45 levels and the wide arithmetic in [Dd.cs](../../Dd.cs) after about 100 — which is the deepest budget of any formula in the file.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal "sierpinski t" --zoom 1.6
```

[← All twenty-six](../../README.md#every-fractal)
