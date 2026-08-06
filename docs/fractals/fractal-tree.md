# Fractal Tree

[![Fractal Tree](../gallery/fractal-tree.jpg)](../gallery/fractal-tree.jpg)

A trunk, then two smaller copies of the whole tree at an angle to it. The simplest branching rule there is.

## The rule

A branch is a point, a direction and a length. Each one draws itself and then starts two more from its
tip, each turned by $\pm\theta$ and shortened by a factor $r$:

$$\ell_{n+1} = r\,\ell_n, \qquad \phi_{n+1} = \phi_n \pm \theta$$

with $r = 0.72$ and $\theta = 0.42$ radians (about 24°) here. Two numbers decide the whole shape.

### How far the tree reaches

A branch of length $\ell$ has descendants of length $r\ell, r^2\ell, \dots$, so however they turn, none
of them gets further from its tip than the sum

$$\ell\left(r + r^2 + r^3 + \cdots\right) = \ell\,\frac{r}{1-r} = \ell \cdot \frac{0.72}{0.28} \approx 2.57\ell$$

That is a bound, not an estimate, and it is what makes drawing the tree tractable: a branch whose box,
grown by 2.57 of its own lengths, misses the window cannot have any descendant on screen, so the whole
subtree can be dropped. The `2.6` in [DrawnFractals.cs](../../DrawnFractals.cs) is this series.

### Total length is infinite; the canopy has area

Level $n$ has $2^n$ branches of length $r^n\ell$, so the total length is

$$\ell\sum_{n\ge 0} (2r)^n$$

which converges only when $2r < 1$. At $r = 0.72$ we have $2r = 1.44$, so the total length of the tree
is **infinite** inside a bounded region.

The tips are more interesting. They form the attractor of the two maps, and for a rule made of $N$
similarities of ratio $r$ the dimension is $D = \log N/\log(1/r)$ — here

$$D = \frac{\log 2}{\log(1/0.72)} \approx 2.11$$

A dimension above 2 is impossible for a subset of the plane, and what that really says is that the
copies **overlap**: the open set condition fails, the formula stops applying, and the true dimension is
capped at 2. Which is not a technicality you can ignore — it is visible. Above
$r = 1/\sqrt2 \approx 0.707$ the branch tips stop being a dust and become a set with positive area,
and at 0.72 we are just
past that threshold. That is why the canopy in the picture is a solid sheet with a smooth outer edge
rather than a spray of separate twigs, and why lowering the shrink slightly would break it into one.

## Where it came from

Binary branching is old as a picture and hard to attribute, but its formal home is the **L-system**, introduced by the Hungarian biologist **Aristid Lindenmayer** in 1968 to model how algae and plants grow by rewriting strings of symbols. Lindenmayer and Przemysław Prusinkiewicz's *The Algorithmic Beauty of Plants* (1990) is where this kind of rule became a discipline rather than a doodle.

## Worth knowing

The reason it looks like a tree is not that it was designed to. Branching that fills space efficiently has to divide at roughly these ratios, so a rule chosen for simplicity and a tree shaped by selection arrive at the same picture — which was Lindenmayer's point.

## How this program draws it

The tree is the reason this file walks its rules a level at a time rather than depth first. A branch's descendants sprawl about two and a half of its own lengths past its tip, so while the branches are longer than the window nothing can be culled and the count doubles every level; with a single shared budget spent depth first, the first subtree got everything and its siblings got nothing — the tree drew its right-hand side in full detail and left the left side empty.

It is also where the batching came from. Drawing each branch as it was found meant sixteen thousand `DrawLine` calls a frame, each preceded by a colour and width change so Skia could batch none of them: **11.3 fps** at the opening view, before any zooming. Collecting segments into one path per depth took it to **99.6 fps**. See [DrawnFractals.cs](../../DrawnFractals.cs).

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal fractal
```

[← All twenty-six](../../README.md#every-fractal)
