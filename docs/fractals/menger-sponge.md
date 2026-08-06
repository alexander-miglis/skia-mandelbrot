# Menger Sponge

[![Menger Sponge](../gallery/menger-sponge.jpg)](../gallery/menger-sponge.jpg)

A cube with the middle of every face and its core removed, twenty of the twenty-seven sub-cubes kept, forever.

## The rule

Divide a cube into $3 \times 3 \times 3 = 27$ sub-cubes. Remove the one at the centre of each face and
the one in the middle — seven in all — keeping 20. Repeat on each.

### Dimension, volume and area

Twenty copies at ratio $\tfrac13$:

$$D = \frac{\log 20}{\log 3} \approx 2.7268$$

After $n$ steps the volume is $\left(\tfrac{20}{27}\right)^n \to 0$ while the surface area grows without
bound. The sponge is the limit: **zero volume, infinite surface area**, and a dimension between a surface
and a solid. Every face of it is a [Sierpiński carpet](sierpinski-carpet.md).

### Universality

Menger built it while working out what "dimension" should mean for sets that are not manifolds, and it
answers that with the strongest statement available. The sponge is a **universal curve**: every compact
metric space of topological dimension $\le 1$ — every knot, every graph, every tangle, in any number of
dimensions — is homeomorphic to a subset of it. The [carpet](sierpinski-carpet.md) does the same job for
sets embeddable in the plane; the sponge drops that restriction entirely.

### The distance estimator

The usual formulation (Iñigo Quílez's) does not remove cubes at all. It folds the point into one
sub-cube by taking $|\,\cdot\,|$ and sorting the coordinates, then measures against a **cross** — the
shape actually removed at each step — and takes the maximum against the running distance, once per
level:

$$d \leftarrow \max\bigl(d,\ \text{cross-distance at this scale}\bigr)$$

Intersecting with the complement of a shape is a maximum of signed distances, so what looks like a
subtractive construction becomes a handful of maxima and absolute values in the shader.

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

**Karl Menger** described it in 1926, while working on what "dimension" ought to mean for sets that are not manifolds. Any face of it is a [Sierpiński carpet](sierpinski-carpet.md).

## Worth knowing

It is a **universal curve**: every one-dimensional compact set, of any topology, in any number of dimensions, embeds in it. Every knot, every graph, every tangle you can imagine is somewhere in this one sponge.

In October 2014 the **MegaMenger** project, run by Matt Parker and Laura Taalman, built a level-four sponge distributed across more than twenty sites worldwide, out of folded business cards — six cards per small cube, and on the order of a million cards in total.

## How this program draws it

Ray-marched on the card with a **distance estimator**: rather than testing points for membership, the shader asks "how far can I safely step without hitting anything?" and takes that step, over and over, until the distance falls under a threshold. The surface normal — and so the shading — comes from the gradient of the same estimate. See the march shader in [GpuKernel.cs](../../GpuKernel.cs).

These six read the flat controls as a camera: the pan offsets become yaw and pitch, and the zoom becomes camera distance, so dragging and scrolling orbit the object. They need the card — there is no CPU counterpart — and they are the one group in this program where `--center 0,0` is a bad view rather than a neutral one.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal menger --center 4.0,0.25 --zoom 2.6
```

[← All twenty-six](../../README.md#every-fractal)
