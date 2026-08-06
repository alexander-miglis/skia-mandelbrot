# Lyapunov

[![Lyapunov](../gallery/lyapunov.jpg)](../gallery/lyapunov.jpg)

Not an escape time and not even a set: a map of how chaotic the logistic map is, drawn over the plane of two growth rates.

## The rule

Not an escape time and not a set of points in the plane — a measurement, plotted over a plane of two
parameters.

### The logistic map and its exponent

Iterate $x_{n+1} = r\,x_n(1-x_n)$ on $[0,1]$. Whether nearby starting points converge or separate is
decided by the derivative $f'(x) = r(1-2x)$ along the orbit: after $N$ steps a small displacement is
multiplied by $\prod_{n} f'(x_n)$. The **Lyapunov exponent** is the average logarithm of that,

$$\lambda = \lim_{N\to\infty} \frac{1}{N}\sum_{n=1}^{N} \ln\bigl|r\,(1 - 2x_n)\bigr|$$

so a displacement grows like $e^{\lambda N}$. Negative $\lambda$ means nearby points fall together: the
orbit settles into a stable cycle. Positive $\lambda$ means they pull apart exponentially — chaos, and
sensitive dependence on initial conditions in its literal, quantitative form.

### Two rates instead of one

Now alternate $r$ between two values $A$ and $B$ on a fixed repeating schedule — this program uses
`AABAB` — and plot $\lambda$ over the $(A,B)$ plane. That is the whole fractal. The stable regions
($\lambda < 0$) carry the colour and the chaotic ones are left black.

The forcing string is a genuine parameter of the picture, and the only one of its kind here: change
`AABAB` to something else and the same mathematics produces a different fractal. The swallow-like shapes
this one is known for belong to that string.

### Why perturbation cannot help it, and wide arithmetic can

Every deep-zoom shortcut in this program depends on the iteration being analytic in the parameter, so
that a nearby pixel's orbit can be written as a small correction to a reference orbit. Here the
iteration is chaotic *by construction* — the interesting region is exactly where nearby orbits diverge
exponentially — so a reference orbit stops representing its neighbours after a handful of steps. There is
nothing to derive.

Wider arithmetic asks for none of that. It simply carries more digits of $x$, which is why this fractal
zooms in this program at all. The logarithms stay in double precision: a few thousand of them are summed
and divided by their count, so their individual rounding washes out, while the map itself needs the
width.

## Where it came from

**Mario Markus** and Bruno Hess published it in 1989 — "Lyapunov exponents of the logistic map with periodic forcing", Computers & Graphics 13(4), 553–558 — from the Max Planck Institute of Molecular Physiology in Dortmund. It reached a wide audience through A. K. Dewdney's "Leaping into Lyapunov space" in *Scientific American* in September 1991.

## Worth knowing

The swallow-like shapes it is known for depend entirely on the forcing sequence. `AABAB` gives the classic ones; change the string and you get a different picture from the same mathematics, which makes it the only fractal here with a *word* as a parameter.

It is also the clearest case in this program of a fractal that perturbation theory cannot help. The iteration is not analytic in the parameters in the way perturbation needs, and a chaotic orbit is the opposite of the well-behaved reference orbit the technique depends on. Wider arithmetic works because it needs no such assumption.

## How this program draws it

The orbit is settled for 24 iterations before the exponent is accumulated, so what is measured is the attractor rather than the transient. Positive exponents return "interior" and read as black; negative ones are scaled into the palette, so deeper blues are more strongly stable.

The logistic map runs in [Dd.cs](../../Dd.cs) arithmetic below the double floor, but the logarithms stay in doubles: a few thousand of them are summed and then divided by their count, so their individual error washes out.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal lyapunov
```

[← All twenty-six](../../README.md#every-fractal)
