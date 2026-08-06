# Multibrot

[![Multibrot](../gallery/multibrot.jpg)](../gallery/multibrot.jpg)

The Mandelbrot with the exponent turned up: `z → z³ + c` here, and the set grows an extra lobe.

## The rule

$$z_{n+1} = z_n^{\,d} + c$$

from $z_0 = 0$, with $d = 3$ here. The quadratic case is $d=2$; everything else is a multibrot.

### Symmetry from the exponent

Replace $c$ by $\omega c$ where $\omega^{d-1} = 1$, and substitute $z \to \omega z$: then
$(\omega z)^d + \omega c = \omega^d z^d + \omega c = \omega\left(z^d + c\right)$, using
$\omega^{d} = \omega$. The whole orbit is rotated by $\omega$, so it escapes exactly when the original
does. The set therefore has **$(d-1)$-fold rotational symmetry** — one lobe for the quadratic, two for
the cubic, and so on toward a disc with a frilled edge as $d$ grows.

### The escape radius and the potential both move

The bailout argument needs $|z|^d - |c| > |z|$, so the safe radius is the solution of
$R^d - R = |c|$ rather than 2. And the smooth count changes base. Past the bailout the orbit is
essentially $z \mapsto z^d$, which multiplies $\log|z|$ by $d$ each step, so $\log_d \log|z_n|$ — not
$\log_2$ — is what advances by one per iteration:

$$\nu = n + 1 - \frac{\log \log |z_n|}{\log d}$$

Get that base wrong and the bands come out unevenly spaced, brightening or compressing as the orbit
escapes faster. It is the one place in this program where the exponent shows up in the *colouring*
rather than in the iteration, and [Mandelbrot.cs](../../Mandelbrot.cs) carries $\log 3$ as a constant
for it.

## Where it came from

There is no single discoverer to name. The generalisation is the obvious next question once you have the quadratic case, and it was explored from the earliest days of fractal imaging in the 1980s; the name "multibrot" belongs to that popular literature rather than to a paper.

## Worth knowing

The smooth escape count is where the exponent shows up in the *code* rather than the mathematics. The usual formula `n + 1 − log₂(log|z|)` has a base-2 logarithm in it because the orbit squares each step; at degree three it has to become a base-3 one, or the bands come out unevenly spaced. Every degree needs its own constant.

## How this program draws it

Iterated as `z(r² − 3i²) + cr`, which is the cubic written out to avoid a general power function, and the smooth count divides by `log 3` instead of taking `log₂`. Below the double floor it uses the wide arithmetic in [Dd.cs](../../Dd.cs) — measured at 16 distinct values across 64 pixels at a scale of 1e-20, where plain doubles give one.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal multibrot
```

[← All twenty-six](../../README.md#every-fractal)
