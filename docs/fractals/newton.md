# Newton

[![Newton](../gallery/newton.jpg)](../gallery/newton.jpg)

Not an escape time at all: colour each point by *which* root Newton's method carries it to, and how long it took.

## The rule

Newton's method for a root of $f$ replaces a guess with the point where the tangent crosses zero:

$$z_{n+1} = z_n - \frac{f(z_n)}{f'(z_n)}$$

Take $f(z) = z^3 - 1$, whose roots are the three cube roots of unity, and start from the pixel:

$$z_{n+1} = z_n - \frac{z_n^3 - 1}{3z_n^2} = \frac{2z_n^3 + 1}{3z_n^2}$$

Colour by *which* root the orbit reaches. The plane divides into three **basins of attraction**, and
their common boundary is the fractal.

### Why it converges, and why the boundary is wild

Near a simple root the method is quadratically convergent: writing $z_n = \rho + \varepsilon_n$,

$$\varepsilon_{n+1} = \frac{f''(\rho)}{2f'(\rho)}\varepsilon_n^2 + O(\varepsilon_n^3)$$

so the error squares each step and the number of correct digits doubles — the roots are **superattracting
fixed points** of the iteration. Every one has an open basin around it. The interesting question is what
happens between them.

The answer is forced by symmetry. The three basins are permuted by rotation through $120°$, so no point
of the boundary can belong to two basins without belonging to all three. A boundary point therefore has
points of all three colours arbitrarily close to it — and a curve dividing three regions in that way,
everywhere, cannot be smooth. It is the Julia set of the Newton map, and it has to be fractal.

### What that means for zooming

The value coloured here is a whole iteration count and a root index, not a continuous escape time. In
the middle of a basin every point converges in the same number of steps, so a deep view there is a flat
wash: the structure lives entirely on the boundary. This program will follow you down — the arithmetic
widens below $10^{-12}$ — but there is less to find than in an escape-time set, and it has to be aimed
at the boundary to find any of it.

## Where it came from

This is the oldest question in the file. **Arthur Cayley** asked it in 1879, in "The Newton–Fourier imaginary problem" (American Journal of Mathematics), after **Ernst Schröder** had studied the same iteration in 1870–71. Cayley solved the quadratic case completely — two basins, divided by a straight line — and reported that the cubic case presented considerable difficulty. It did. The answer needed Fatou and Julia forty years later, and a picture of it needed another sixty.

## Worth knowing

Cayley's difficulty is the whole subject in miniature. Nothing about `z³ − 1` suggests that the boundary between its three basins should be an infinitely detailed curve, and there was no way to suspect it from the algebra. The 1870s had the question and could not have the answer.

## How this program draws it

Convergence is detected by the step size falling below a threshold, and the palette is shifted by which root was reached, so the three basins read as three families of colour rather than one.

A consequence worth knowing before you zoom: the value coloured here is a whole iteration count, not a continuous escape time, so a deep view of a basin *interior* is flat. The detail is all on the boundary. The arithmetic will follow you down — it switches to [Dd.cs](../../Dd.cs) below 1e-12 — but there is less to find than in an escape-time set.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal newton
```

[← All twenty-six](../../README.md#every-fractal)
