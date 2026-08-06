# Tricorn

[![Tricorn](../gallery/tricorn.jpg)](../gallery/tricorn.jpg)

Also called the Mandelbar: conjugate `z` before squaring, and the set grows three corners.

## The rule

$$z_{n+1} = \overline{z_n}^{\,2} + c$$

started from zero. Conjugation reflects across the real axis, so each step turns the plane the *other*
way before squaring. Writing $z = x+iy$:

$$x_{n+1} = x_n^2 - y_n^2 + \operatorname{Re} c, \qquad y_{n+1} = -2x_n y_n + \operatorname{Im} c$$

— the Mandelbrot's iteration with one sign flipped, which is all this program has to do to render it.

### Where the three corners come from

Write $f_c(z) = \bar z^{\,2} + c$ and let $\omega = e^{2\pi i/3}$, a cube root of 1. Substitute
$z \to \omega z$ into the map for the parameter $\omega c$:

$$\frac{1}{\omega} f_{\omega c}(\omega z) = \frac{\overline{\omega z}^{\,2} + \omega c}{\omega}
= \frac{\bar\omega^{2}\,\bar z^{2}}{\omega} + c$$

and since $\bar\omega = \omega^{-1} = \omega^{2}$, the coefficient
$\bar\omega^{2}/\omega = \omega^{4}/\omega = 1$. So

$$\frac{1}{\omega} f_{\omega c}(\omega z) = \bar z^{\,2} + c = f_c(z)$$

The two maps are the same map in different coordinates. Their orbits escape together, so $c$ is in the
set exactly when $\omega c$ is: the tricorn has **three-fold rotational symmetry**, and that is where
the corners come from. Run the same substitution on $z^2 + c$ and it fails — the quadratic map only
gives back the reflection $c \to \bar c$, which is why the Mandelbrot set has one cardioid and a mirror
line where this has three of everything.

### What survives from the holomorphic case

Not much, but the useful parts. The map is **anti-holomorphic** — conjugation reverses orientation, so
a single step is not complex-differentiable while the composition of two steps is — and the theory that
proved the Mandelbrot set connected assumes holomorphy throughout. What does carry over is anything
depending only on moduli: $|\bar z| = |z|$, so the escape radius argument and the smooth potential
$\nu = n + 1 - \log_2\log|z_n|$ are unchanged, and rendering it costs one sign flip.

## Where it came from

Introduced as the *Mandelbar set* by Crowe, Hasson, Rippon and Strain-Clark in 1989, in "On the structure of the Mandelbar set" (Nonlinearity 2(4), 541–553). John Milnor later ran into tricorn-like sets from a completely different direction — as a recurring configuration in the parameter space of real cubic polynomials, and in other families of rational maps — which is part of why the shape is taken seriously rather than treated as a curiosity.

## Worth knowing

Its boundary behaves unlike the Mandelbrot's in ways that took years to establish. Where the Mandelbrot set is connected and conjectured to be locally connected, the tricorn and its higher-degree relatives are known **not to be path connected** — a result of Hubbard and Schleicher's. Anti-holomorphic dynamics turned out to be its own subject rather than a footnote to the holomorphic case.

## How this program draws it

One code path serves the Mandelbrot, the Julia and the Tricorn — the same quadratic step with a sign on the imaginary term, which is all conjugation amounts to here. See the `conjugate` parameter in [Mandelbrot.cs](../../Mandelbrot.cs).

The gallery view is pulled *back* from the opening one (`--zoom 0.7`), because at the default width the third corner is off the bottom of the frame.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal tricorn --center 0,0 --zoom 0.7
```

[← All twenty-six](../../README.md#every-fractal)
