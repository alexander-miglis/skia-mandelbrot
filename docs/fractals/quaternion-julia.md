# Quaternion Julia

[![Quaternion Julia](../gallery/quaternion-julia.jpg)](../gallery/quaternion-julia.jpg)

A Julia set built in the quaternions, where numbers have four components — so the set is four-dimensional and what you see is a slice.

## The rule

The same `z → z² + k` as an ordinary [Julia set](julia.md), but `z` and `k` are quaternions: four real components, with a multiplication that is associative but **not commutative**. The set lives in four dimensions; this renders a three-dimensional slice of it, which is why the surfaces look poured and shell-like rather than encrusted — you are seeing a cross-section of something whose detail partly points in a direction not on screen.

## Where it came from

**Alan Norton**, at IBM Research, published quaternion Julia set images in 1982 — among the earliest three-dimensional fractal renderings anyone made. In 1989 **John Hart**, with Dan Sandin and Louis Kauffman, published "Ray tracing deterministic 3-D fractals", which introduced the **unbounding volume**: step along a ray by a distance guaranteed smaller than the distance to the set, and you can never overshoot it.

## Worth knowing

That 1989 paper is the direct ancestor of how every three-dimensional fractal in this program is drawn. Distance-estimated sphere tracing is now standard for real-time shader work far beyond fractals, and it started as a way to ray-trace this object.

## How this program draws it

Ray-marched on the card with a **distance estimator**: rather than testing points for membership, the shader asks "how far can I safely step without hitting anything?" and takes that step, over and over, until the distance falls under a threshold. The surface normal — and so the shading — comes from the gradient of the same estimate. See the march shader in [GpuKernel.cs](../../GpuKernel.cs).

These six read the flat controls as a camera: the pan offsets become yaw and pitch, and the zoom becomes camera distance, so dragging and scrolling orbit the object. They need the card — there is no CPU counterpart — and they are the one group in this program where `--center 0,0` is a bad view rather than a neutral one.

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal quaternion --center 0.6,0.4 --zoom 4
```

[← All twenty-six](../../README.md#every-fractal)
