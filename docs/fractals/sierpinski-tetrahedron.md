# Sierpiński Tetrahedron

[![Sierpiński Tetrahedron](../gallery/sierpinski-tetrahedron.jpg)](../gallery/sierpinski-tetrahedron.jpg)

The [triangle](sierpinski-triangle.md) in three dimensions: four half-size copies at the corners, and the middle left empty.

## The rule

Four copies of a tetrahedron at half the size, one at each vertex, and the middle left empty. Repeat.

### A fractal whose dimension is an integer

Four copies at ratio $\tfrac12$:

$$D = \frac{\log 4}{\log 2} = 2$$

Exactly two — the same as a flat sheet of paper, arrived at by a construction that is plainly not flat
and plainly not solid. Its volume is zero: each step keeps 4 of the 8 half-size sub-tetrahedra, so the
volume is multiplied by $4 \cdot \left(\tfrac12\right)^3 = \tfrac12$ each time. Meanwhile its surface
area is *constant*, since the four faces of each copy exactly reproduce the four faces of the parent at
half the linear size, four times over: $4 \times \tfrac14 = 1$.

So this is a set with zero volume, unchanging surface area, and dimension precisely 2. It is a fractal
that is not fractional, which is a useful corrective to the name: "fractal" means self-similar detail at
every scale, not necessarily a non-integer dimension.

Look at it along an edge and it closes up into a solid-looking square, with no gaps at all. That is the
dimension being 2, made visible — a projection of it fills a two-dimensional region.

### The distance estimator

Folding rather than subdividing, as everything ray-marched here does. Reflect the point in the three
planes bisecting the tetrahedron's symmetry, then scale about the nearest vertex by 2 and repeat:

$$\mathbf{p} \leftarrow 2\mathbf{p} - \mathbf{v}\,(2-1), \qquad DE = \frac{\|\mathbf{p}\|}{2^{\,n}} - r$$

Each fold divides space into the region belonging to one sub-tetrahedron and maps it onto the whole, so
$n$ folds resolve $n$ levels at the cost of $n$ comparisons.

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

The natural three-dimensional analogue of Sierpiński's 1915 construction, and old enough as an object that it is not really attributable to anyone in particular.

## Worth knowing

Its dimension is **exactly 2** — log 4 / log 2 — which makes it the neatest curiosity in this list. It is a fractal with an integer dimension: the same dimension as a flat sheet of paper, occupying three-dimensional space, with zero volume. Look at it along an edge and it really does close up into a solid-looking square, which is the dimension being two showing itself.

## How this program draws it

Ray-marched on the card with a **distance estimator**: rather than testing points for membership, the shader asks "how far can I safely step without hitting anything?" and takes that step, over and over, until the distance falls under a threshold. The surface normal — and so the shading — comes from the gradient of the same estimate. See the march shader in [GpuKernel.cs](../../GpuKernel.cs).

These six read the flat controls as a camera: the pan offsets become yaw and pitch, and the zoom becomes camera distance, so dragging and scrolling orbit the object. They need the card — there is no CPU counterpart — and they are the one group in this program where `--center 0,0` is a bad view rather than a neutral one.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal "sierpinski te" --center 0.6,0.4 --zoom 1.6
```

[← All twenty-six](../../README.md#every-fractal)
