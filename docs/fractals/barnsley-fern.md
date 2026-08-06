# Barnsley Fern

[![Barnsley Fern](../gallery/barnsley-fern.jpg)](../gallery/barnsley-fern.jpg)

Four affine maps, applied at random, that draw a fern. The picture is the attractor and nothing else is stored.

## The rule

### Affine maps

An **affine map** of the plane is a matrix and a shift:

$$f(\mathbf{p}) = A\mathbf{p} + \mathbf{t}, \qquad A = \begin{pmatrix} a & b \\ c & d \end{pmatrix}$$

The matrix does everything that keeps straight lines straight and parallel lines parallel — scaling,
rotation, shear, reflection — and the shift moves the result. Two facts about $A$ carry most of the
weight here. Its **determinant** $ad - bc$ is the factor by which it multiplies *area*, and a negative
one means the map also flips the plane over. And the map **contracts** — pulls every pair of points
closer together — when its largest singular value is under 1.

The fern is four of these. Read each one as an instruction and the plant appears.

**The stem.** Determinant zero — it squashes the entire plane flat onto a vertical segment, keeping 16%
of the height and none of the width:

$$f_1(\mathbf{p}) = \begin{pmatrix} 0 & 0 \\ 0 & 0.16 \end{pmatrix}\mathbf{p}$$

**The stalk, and everything above it.** This one has the form
$\begin{pmatrix} a & b \\ -b & a \end{pmatrix}$, which is *exactly* a rotation times a scale — no
shear, no distortion. The scale is
$\sqrt{a^2+b^2} = \sqrt{0.85^2 + 0.04^2} \approx 0.851$ and the angle is
$\arctan(0.04/0.85) \approx 2.7°$; the determinant, $a^2 + b^2 = 0.724$, is that scale squared. So it
says *take the whole fern,
make it 15% smaller, tilt it a couple of degrees, and set it 1.6 further up*. Apply that over and over
and you have climbed the stem, each copy slightly smaller and slightly more bent than the last — which
is the curve of the frond:

$$f_2(\mathbf{p}) = \begin{pmatrix} 0.85 & 0.04 \\ -0.04 & 0.85 \end{pmatrix}\mathbf{p}
+ \begin{pmatrix} 0 \\ 1.6 \end{pmatrix}$$

**The two bottom leaflets.** Both shrink the fern to roughly a third and swing it out to one side.
Determinants $0.104$ and $-0.109$ — and that minus sign is the whole difference between them. A negative
determinant means the map turns the plane over, so the left leaflet is a *mirror image* of the fern
rather than a rotation of it:

$$f_3(\mathbf{p}) = \begin{pmatrix} 0.20 & -0.26 \\ 0.23 & 0.22 \end{pmatrix}\mathbf{p}
+ \begin{pmatrix} 0 \\ 1.6 \end{pmatrix}, \qquad
f_4(\mathbf{p}) = \begin{pmatrix} -0.15 & 0.28 \\ 0.26 & 0.24 \end{pmatrix}\mathbf{p}
+ \begin{pmatrix} 0 \\ 0.44 \end{pmatrix}$$

Four maps, twenty-four numbers, and every one of them is doing something you can point at in the
picture.

### Why the four of them have a fern in them

Take any compact set $S$ and apply all four, keeping the union:

$$W(S) = f_1(S) \cup f_2(S) \cup f_3(S) \cup f_4(S)$$

The **fern** is the set that comes back unchanged, $W(F) = F$. Not *a* set with that property — *the*
set. The reason is Hutchinson's, and it is the contraction mapping theorem applied one level up. Take
the space whose *points* are compact subsets of the plane, with the Hausdorff distance between two sets
as the metric. In that space $W$ is a contraction with ratio $\max_i r_i = 0.85$, and a contraction on
a complete metric space has exactly one fixed point, reached from any starting point.

So the fern does not depend on where you begin. Start with a square, a circle, a photograph — apply $W$
repeatedly and the sequence converges to the same set, and after $n$ steps you are within
$0.85^{\,n}$ of it. Which is also why the picture is only twenty-four numbers.

### Why throwing random points draws it

Instead of transforming whole sets, pick one map at a time at random and follow a single point:

$$\mathbf{p}_{n+1} = f_{i_n}(\mathbf{p}_n), \qquad i_n \text{ chosen with probability } p_i$$

After $n$ steps that point is $f_{i_n} \circ \cdots \circ f_{i_1}(\mathbf{p}_0)$ — a composition of $n$
contractions, so whatever distance the starting point was from the fern has been multiplied by at most
$0.85^{\,n}$. After fifty steps that is a factor of $3\times10^{-4}$, after a hundred $10^{-7}$: the
starting point is forgotten and every point plotted from then on is *on* the fern to well within a
pixel. This program throws away no points at all, because at 240,000 of them the first few are not
worth the branch.

The probabilities do not change *which* set gets drawn — only how the walk spends its time on it. They
are chosen close to $|\det A_i|$, since a map that squeezes area by a factor of ten needs a tenth of
the visits to cover its image at the same density. Hence 85% to $f_2$ and 1% to the stem: the stem is
tiny in area and still comes out solid.

### Address, and where the self-similarity is

Every point of the fern is the limit of some infinite sequence of map choices, and that sequence is its
**address**. Points sharing a first letter lie in the same top-level piece — all the $f_2$ addresses
make up the copy of the fern one notch up the stalk. That is the structure the box walk in this program
follows, level by level, and why the map applied *first* is the one that decides which large piece a
point is in.

The self-similarity is exact along the stalk and not elsewhere, and the difference is visible. $f_2$ is a
true similarity, so each copy up the stem is the whole plant reduced and turned, undistorted — but
turned by 2.7° *every* time, and those rotations add up, which is why a frond near the tip leans
noticeably further over than one near the bottom. $f_3$ and $f_4$ are not similarities at all: their
matrices are not of the $\begin{pmatrix} a & b \\ -b & a\end{pmatrix}$ form, so they shear as well as
shrink, and the bottom leaflets are slightly distorted copies rather than faithful ones.

The box dimension is about **1.8**, and there is no tidy $\log N/\log(1/r)$ for it as there is for
[Koch](koch-snowflake.md): that formula needs $N$ copies at one common ratio, and these are four maps
with four different ratios whose images overlap.

## Where it came from

**Michael Barnsley** set it out in *Fractals Everywhere* (1988), as the showpiece for iterated function systems and the **chaos game**. His larger claim was the collage theorem: if you can cover a shape with shrunken copies of itself, those copies' coefficients are a description of it — which made a genuine attempt at fractal image compression, twenty-four numbers in place of a photograph of a fern.

## Worth knowing

The probabilities are not the shape — they are the *sampling*. The fern is the same set whatever weights you use; the weights only decide how the random walk spends its time, which is why the stem gets 1% and still comes out solid, being so much smaller than the rest.

## How this program draws it

Two renderers, chosen by measurement. Throwing points is the classic method and much the cheapest — one multiply-add each, no recursion, and the density does the shading — but it cannot be zoomed, since a random point lands in a window a billionth of the fern across about a billionth of the time. So it runs first, and if too few points landed on screen the fern is walked as **nested boxes** instead, which covers whatever part of it is visible at any magnification.

That walk had a bug worth recording, because it is easy to write and looks nearly right: it applied each new map on the *outside*, which is the order the iteration itself runs in — but `m(parent)` is not inside `parent`, so a node does not contain its descendants and culling one throws away pieces that are on screen. A deep view of the fern's tip drew a single point.

It is also the one fractal here coloured like the thing it depicts rather than from the shared gradient, and which part is stem falls out of the rule: a point is on a stem if its recent history includes the flattening map, and how recently says which stem. See [DrawnFractals.cs](../../DrawnFractals.cs).

## See it

```bash
dotnet run -c Release -- --explore --no-menu --fractal barnsley
```

[← All twenty-six](../../README.md#every-fractal)
