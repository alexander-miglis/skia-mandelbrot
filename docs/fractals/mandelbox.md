# Mandelbox

[![Mandelbox](../gallery/mandelbox.jpg)](../gallery/mandelbox.jpg)

Not a formula that escapes — a space that folds. Two folds and a scale, repeated, and the result looks built rather than grown.

## The rule

Nothing here is raised to a power. Each iteration applies two folds and a scale:

$$\mathbf{p} \ \leftarrow\ s\cdot \mathrm{sphereFold}\bigl(\mathrm{boxFold}(\mathbf{p})\bigr) + \mathbf{c}$$

**Box fold** — reflect any coordinate that has gone past a limit back inside, componentwise:

$$x \mapsto \begin{cases} 2L - x & x > L \\ -2L - x & x < -L \\ x & \text{otherwise}\end{cases}$$

**Sphere fold** — an inversion in a sphere, applied only to points that are too close in:

$$\mathbf{p} \mapsto \begin{cases}
\mathbf{p}\cdot \dfrac{R^2}{r_{\min}^2} & \|\mathbf{p}\| < r_{\min} \\[6pt]
\mathbf{p}\cdot \dfrac{R^2}{\|\mathbf{p}\|^2} & r_{\min} \le \|\mathbf{p}\| < R \\[6pt]
\mathbf{p} & \text{otherwise}
\end{cases}$$

Then multiply by the scale $s$ and add the starting point back.

### Why folding produces structure at all

Both folds are **isometries or conformal maps that are only piecewise smooth**: a reflection is
distance-preserving, an inversion preserves angles. Neither creates detail on its own. What creates it is
the *seams* — the planes where the box fold switches branch, and the spheres where the inversion does.
Each iteration maps those seams into the previous ones, so after $n$ steps there are seams at every scale
down to $s^{-n}$. The surfaces you see are the accumulated discontinuities of a piecewise map, not the
level set of a smooth function, which is exactly why the object looks *machined* — flat panels, straight
corridors and right angles — where every other fractal here looks grown.

Because every operation is a similarity or an inversion, the distance estimate is exact and cheap: track
the derivative as a single scalar $dr$, multiplied by the same factors the point is, and
$DE = \|\mathbf{p}\| / |dr|$.

### The scale changes everything

$s$ is a free parameter and the object is a different one for each value. Negative $s$ gives a
completely different shape; near $|s| = 2$ the folds balance and the structure is at its most intricate.
"The" Mandelbox is really a one-parameter family.

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

**Tom Lowe** — "Tglad" — introduced it on Fractal Forums in early 2010, from experiments with whether iterated folding could produce bounded self-similar structure the way the Mandelbrot iteration does in two dimensions. It could, and the results looked like nothing else in the field.

## Worth knowing

It is the fractal that looks man-made. Every other object here is knobbly, branched or encrusted; the Mandelbox has hallways, balconies and machined-looking panels, and it got adopted almost immediately by animators for exactly that reason. The scale parameter changes it drastically — negative values give a completely different object — so "the" Mandelbox is really a family.

## How this program draws it

Ray-marched on the card with a **distance estimator**: rather than testing points for membership, the shader asks "how far can I safely step without hitting anything?" and takes that step, over and over, until the distance falls under a threshold. The surface normal — and so the shading — comes from the gradient of the same estimate. See the march shader in [GpuKernel.cs](../../GpuKernel.cs).

These six read the flat controls as a camera: the pan offsets become yaw and pitch, and the zoom becomes camera distance, so dragging and scrolling orbit the object. They need the card — there is no CPU counterpart — and they are the one group in this program where `--center 0,0` is a bad view rather than a neutral one.

The gallery view uses `--zoom 0.5`, which pulls the camera *back* rather than in. Values below 1 were rejected by the flag until this gallery needed one.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal mandelbox --center 2.2,0.8 --zoom 0.5
```

[← All twenty-six](../../README.md#every-fractal)
