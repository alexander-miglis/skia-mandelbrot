# Lyapunov

[![Lyapunov](../gallery/lyapunov.jpg)](../gallery/lyapunov.jpg)

Not an escape time and not even a set: a map of how chaotic the logistic map is, drawn over the plane of two growth rates.

## The rule

Iterate the logistic map `x → r·x·(1 − x)`, but alternate `r` between two values **A** and **B** on a fixed schedule — this program uses `AABAB`. Measure the Lyapunov exponent of the resulting orbit: the average of `log|dx'/dx|`, which says whether nearby starting points pull apart (chaos, positive) or fall together (a stable cycle, negative). Plot that over the A–B plane. The stable regions are the structure; the chaotic ones are left black.

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
