# Magnet

[![Magnet](../gallery/magnet.jpg)](../gallery/magnet.jpg)

A formula that came out of statistical physics rather than mathematics: the boundary between a magnet and a non-magnet.

## The rule

`z → ((z² + c − 1) / (2z + c − 2))²`. It is a rational map, so it can do two different things — escape to infinity, or converge to a finite value — and both have to be tested for. Convergence here is to **one** rather than to zero, which is the fixed point that matters physically.

## Where it came from

The formula is a renormalisation transformation for the Ising model of a ferromagnet: iterate it and you are following what happens to the effective temperature of a magnetic material as you look at it on coarser and coarser scales. Its fixed points are the phases, and the boundary between them — the fractal — is the critical point where the material changes from magnetic to not. It is set out in Peitgen and Richter's *The Beauty of Fractals* (1986), p. 129, and reached fractal software through Fractint, contributed by Scott Taylor and Lee Skinner.

## Worth knowing

This one is a genuine physical object rather than a picture that resembles one. The shape on screen is the phase boundary of a model that physicists were solving for reasons entirely unconnected with fractal geometry, and the fact that it is infinitely detailed is a statement about critical phenomena, not about drawing.

## How this program draws it

Both endings are tested every iteration: a bailout radius for escape, and a settling threshold on the step size for convergence. The division needs a guard — the denominator `2z + c − 2` genuinely reaches zero — and where it does the point is treated as interior.

Like [Newton](newton.md), it colours by a whole iteration count where it converges, so its deep views are flatter than an escape-time set's.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal magnet
```

[← All twenty-six](../../README.md#every-fractal)
