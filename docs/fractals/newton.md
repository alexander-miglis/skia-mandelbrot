# Newton

[![Newton](../gallery/newton.jpg)](../gallery/newton.jpg)

Not an escape time at all: colour each point by *which* root Newton's method carries it to, and how long it took.

## The rule

Apply Newton's root-finding step `z → z − f(z)/f'(z)` to `f(z) = z³ − 1`, starting from the pixel. Almost every starting point converges to one of the three cube roots of unity. Colour by which one, and the plane divides into three basins — whose common boundary is the fractal. Every point of that boundary touches all three basins at once.

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
