# Mandelbulb

[![Mandelbulb](../gallery/mandelbulb.jpg)](../gallery/mandelbulb.jpg)

The answer to an obvious question that took thirty years to answer: what does the Mandelbrot set look like in three dimensions?

## The rule

Squaring a complex number doubles its argument and squares its modulus. Write that in polar form and it
generalises to a power $n$ without any reference to complex multiplication:

$$z^n : \quad (r, \theta) \ \longmapsto \ (r^n,\ n\theta)$$

The Mandelbulb takes that literally in three dimensions, using spherical coordinates. For
$\mathbf{p} = (x,y,z)$ with

$$r = \|\mathbf{p}\|, \qquad \theta = \arccos\frac{z}{r}, \qquad \phi = \arctan\frac{y}{x}$$

define

$$\mathbf{p}^{\,n} = r^n\bigl(\sin(n\theta)\cos(n\phi),\ \sin(n\theta)\sin(n\phi),\ \cos(n\theta)\bigr)$$

and iterate $\mathbf{p} \mapsto \mathbf{p}^{\,n} + \mathbf{c}$ exactly as in two dimensions.

### Why this is not a number system

There is no three-dimensional algebra that behaves like the complex numbers. Frobenius' theorem says the
only finite-dimensional associative division algebras over the reals are $\mathbb{R}$, $\mathbb{C}$, and
the quaternions $\mathbb{H}$ — dimensions 1, 2 and 4. Nothing of dimension 3. So the operation above is
not multiplication in any algebra; it is a **triplex** operation, defined to *behave* like squaring —
multiply the angles, power the radius — without obeying the laws that would make it a product. It is not
associative and not distributive.

That is why the search took thirty years. Quaternions were the obvious candidate and give something too
smooth (see [Quaternion Julia](quaternion-julia.md)); the thing that finally worked was to give up on
having an algebra at all and keep only the geometry.

### Why the power is 8

No reason. $n=2$ gives a shape with obvious seams and stretched regions, and the detail improves with
$n$; White and Nylander tried a range and 8 was the one that looked best. Higher powers approach a
sphere with a wrinkled skin. The exponent is a free parameter chosen by eye — unusual for something this
studied, and worth knowing before reading meaning into it.

The distance estimate uses the running derivative $dr$, updated as $dr \leftarrow n\,r^{\,n-1} dr + 1$
alongside the point, giving $DE = \tfrac{1}{2} r \ln r / dr$.

### How any of these get drawn: distance estimation

None of these six is tested point by point. Instead each supplies a **distance estimator** — a function
$DE(\mathbf{p})$ that never overestimates how far $\mathbf{p}$ is from the surface. Given one, a ray is
followed by repeatedly stepping exactly that far:

$$\mathbf{p} \leftarrow \mathbf{p} + DE(\mathbf{p})\cdot \mathbf{d}$$

Because the estimate is a lower bound on the true distance, a step can never jump through the object, and
because it is as large as is safe, empty space is crossed in a handful of steps rather than a thousand
small ones. When $DE$ falls below a threshold, the ray has arrived. This is **sphere tracing**, and John
Hart's 1989 "unbounding volumes" for [quaternion Julia sets](quaternion-julia.md) is where it comes from.

For an escape-time set the estimator is the **Koebe / Douady–Hubbard** distance formula, which needs the
derivative of the iteration carried alongside the point:

$$DE = \frac{|z|\,\ln|z|}{|z'|}, \qquad z'_{n+1} = 2 z_n z'_n \ \text{(for a squaring map)}$$

The surface has no analytic normal either, so the shading comes from the numerical gradient of the same
estimate — sample $DE$ a hair either side along each axis and difference it:

$$\mathbf{n} \approx \text{normalise}\bigl(\nabla DE(\mathbf{p})\bigr)$$

which is why these look lit at all. See the march shader in [GpuKernel.cs](../../GpuKernel.cs).

### The controls are a camera

The flat fractals' pan and zoom are re-read here: the pan offsets become yaw and pitch of an orbit about
the origin, and the zoom becomes camera distance. That is why `--center 0,0` is a *bad* view rather than
a neutral one for these six, and why every gallery shot on this page names an angle.

## Where it came from

Found in 2009 by **Daniel White** and **Paul Nylander**, working in public on Fractal Forums. White posted the spherical "triplex" formulation with renders at powers 2 through 8; Nylander followed almost immediately with the power-8 image that became the canonical Mandelbulb. Neither patented nor trademarked anything, and both shared the credit openly.

## Worth knowing

The wait was not for computers — it was for the right multiplication. Quaternions were tried for decades (see [Quaternion Julia](quaternion-julia.md)) and give something too smooth; the honest three-dimensional analogue of the complex numbers does not exist, so the Mandelbulb is not a number system at all but an operation chosen to *behave* like squaring. The power of 8 has no theoretical justification whatsoever. It was picked because it looked best.

## How this program draws it

Ray-marched on the card with a **distance estimator**: rather than testing points for membership, the shader asks "how far can I safely step without hitting anything?" and takes that step, over and over, until the distance falls under a threshold. The surface normal — and so the shading — comes from the gradient of the same estimate. See the march shader in [GpuKernel.cs](../../GpuKernel.cs).

These six read the flat controls as a camera: the pan offsets become yaw and pitch, and the zoom becomes camera distance, so dragging and scrolling orbit the object. They need the card — there is no CPU counterpart — and they are the one group in this program where `--center 0,0` is a bad view rather than a neutral one.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal mandelbulb --center 0.6,0.4 --zoom 1.6
```

[← All twenty-six](../../README.md#every-fractal)
