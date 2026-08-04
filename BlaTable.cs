using System;
using System.Numerics;

namespace FractalZoom;

/// <summary>
/// Bilinear-approximation table over a reference orbit — the optimisation that makes deep zoom fast
/// rather than merely possible.
///
/// While a pixel's delta is small, the dz^2 term of the perturbed iteration is negligible and the
/// step is effectively linear: dz' = A*dz + B*dc. Linear maps compose, so a run of consecutive
/// steps collapses into a single (A, B) pair, and whole runs can be applied at once. This table
/// holds those pairs for runs of 2, 4, 8, ... steps, each with the radius of |dz| within which it
/// stays accurate, so a pixel can take the longest jump its delta currently allows.
///
/// Interior pixels benefit most: their delta stays tiny for the whole orbit, so they skip nearly
/// everything instead of burning the full iteration budget.
/// </summary>
internal sealed class BlaTable
{
    /// <summary>
    /// Relative error tolerated per skip. The neglected term is dz^2 against a linear part of
    /// 2*Z*dz, so a radius of Epsilon*|Z| keeps the omission at about Epsilon/2 relative.
    ///
    /// Tuned empirically against the step-by-step path, because the per-skip error compounds over
    /// the length of a run: looser values (1e-6) are visibly wrong, and this is the largest that
    /// stayed free of interior/exterior flips at every depth tested. Deep speedups barely depend on
    /// it — a run's length is limited by how far below Epsilon*|Z| the delta is, and at 1e-80 the
    /// delta has 64 decades of headroom either way.
    /// </summary>
    private const double Epsilon = 1e-16;

    private const int MaxLevels = 26;
    private const int Stride = 5; // Ar, Ai, Br, Bi, r^2

    private readonly double[][] _levels;
    private readonly int[] _counts;

    private BlaTable(double[][] levels, int[] counts, double dcMax)
    {
        _levels = levels;
        _counts = counts;
        DcMaxSquared = dcMax * dcMax;
    }

    /// <summary>Largest |dc| the radii were computed for; beyond it the table must not be used.</summary>
    public double DcMaxSquared { get; }

    /// <summary>A collapsed run of reference steps.</summary>
    internal struct Step
    {
        public double Ar, Ai, Br, Bi;
        public int Length;
    }

    /// <summary>
    /// Builds the table for the usable prefix of an orbit. Returns null when the orbit is too short
    /// to be worth it, in which case the caller just iterates normally.
    /// </summary>
    public static BlaTable? Build(double[] zr, double[] zi, int count, double dcMax)
    {
        // Only cover the prefix where the reference is still bounded, so that a skip can never jump
        // over the reference's own escape, and leave room to read Z[m+1] after a step from m.
        int limit = 0;
        for (int m = 1; m + 1 <= count - 1; m++)
        {
            double x = zr[m], y = zi[m];
            if (x * x + y * y > 4.0) break;
            limit = m;
        }

        if (limit < 4) return null;

        // A skip is only ever possible when the linear term can dominate the neglected dz^2 one,
        // which needs |dc| below Epsilon*|Z|. Above that the table would be built, consulted and
        // never used — measurably slower than just iterating.
        if (dcMax >= Epsilon) return null;

        int levels = 1;
        while (levels < MaxLevels && (limit >> levels) >= 1) levels++;

        var table = new double[levels][];
        var counts = new int[levels];

        // Level 0: one step at reference index m is dz' = 2*Z[m]*dz + dz^2 + dc.
        int n = limit;
        counts[0] = n;
        var level0 = new double[n * Stride];
        for (int j = 0; j < n; j++)
        {
            int m = 1 + j;
            double x = zr[m], y = zi[m];
            double r = Epsilon * Math.Sqrt(x * x + y * y);
            int o = j * Stride;
            level0[o] = 2.0 * x;
            level0[o + 1] = 2.0 * y;
            level0[o + 2] = 1.0;
            level0[o + 3] = 0.0;
            level0[o + 4] = r * r;
        }
        table[0] = level0;

        for (int k = 1; k < levels; k++)
        {
            int nk = counts[k - 1] >> 1;
            counts[k] = nk;
            var cur = new double[nk * Stride];
            var prev = table[k - 1];

            for (int j = 0; j < nk; j++)
            {
                int oa = (2 * j) * Stride;       // first half, applied first
                int ob = (2 * j + 1) * Stride;   // second half
                int o = j * Stride;

                double aar = prev[oa], aai = prev[oa + 1];
                double abr = prev[oa + 2], abi = prev[oa + 3];
                double bar = prev[ob], bai = prev[ob + 1];
                double bbr = prev[ob + 2], bbi = prev[ob + 3];

                // A = A_b * A_a,  B = A_b * B_a + B_b
                cur[o] = bar * aar - bai * aai;
                cur[o + 1] = bar * aai + bai * aar;
                cur[o + 2] = bar * abr - bai * abi + bbr;
                cur[o + 3] = bar * abi + bai * abr + bbi;

                // The merged run is valid where the first half is valid AND its output still lands
                // inside the second half's radius: |A_a|*|dz| + |B_a|*dcMax <= r_b.
                double ra = Math.Sqrt(prev[oa + 4]);
                double rb = Math.Sqrt(prev[ob + 4]);
                double magA = Math.Sqrt(aar * aar + aai * aai);
                double magB = Math.Sqrt(abr * abr + abi * abi);

                double allowed = rb - magB * dcMax;
                double r;
                if (allowed <= 0) r = 0;
                else if (magA > 0) r = Math.Min(ra, allowed / magA);
                else r = ra;

                cur[o + 4] = r * r;
            }

            table[k] = cur;
        }

        return new BlaTable(table, counts, dcMax);
    }

    /// <summary>
    /// Longest valid run starting exactly at reference index <paramref name="m"/> for a delta of
    /// magnitude-squared <paramref name="dz2"/>, capped at <paramref name="maxSteps"/>. Level 0 is
    /// skipped deliberately: a single step is cheaper and exact done directly.
    /// </summary>
    public bool TryStep(int m, double dz2, int maxSteps, out Step step)
    {
        step = default;

        int idx = m - 1;
        if (idx < 0 || maxSteps < 2) return false;

        // A level-k run starts at m = 1 + j*2^k, so m-1 must be divisible by 2^k.
        int k = Math.Min(_levels.Length - 1, BitOperations.TrailingZeroCount((uint)idx | 0x8000_0000u));
        while (k >= 1 && (1 << k) > maxSteps) k--;

        for (; k >= 1; k--)
        {
            int j = idx >> k;
            if (j >= _counts[k]) continue;

            var level = _levels[k];
            int o = j * Stride;
            if (level[o + 4] > dz2)
            {
                step.Ar = level[o];
                step.Ai = level[o + 1];
                step.Br = level[o + 2];
                step.Bi = level[o + 3];
                step.Length = 1 << k;
                return true;
            }
        }

        return false;
    }
}
