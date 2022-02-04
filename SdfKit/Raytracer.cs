using System.Buffers;

using static SdfKit.VectorOps;

namespace SdfKit;

public class Raytracer
{
    readonly Sdf sdf;
    readonly int batchSize;
    readonly int maxDegreeOfParallelism;

    readonly int width;
    readonly int height;
    readonly ArrayPool<float> pool = ArrayPool<float>.Create();

    public float ZNear { get; set; } = 3.0f;
    public float ZFar { get; set; } = 1e3f;

    public int DepthIterations { get; set; } = 40;

    public Raytracer(int width, int height, Sdf sdf, int batchSize = Sdf.DefaultBatchSize, int maxDegreeOfParallelism = -1)
    {
        this.width = width;
        this.height = height;
        this.sdf = sdf;
        this.batchSize = batchSize;
        this.maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    /// <summary>
    /// Renders the SDF to an RGB buffer.
    /// </summary>
    public Vec3Data Render()
    {
        GetCameraRays(out var ro, out var rd);
        using (ro)
        using (rd) {
            return Render(ro, rd);
        }
    }

    /// <summary>
    /// Renders the SDF to a depth buffer.
    /// </summary>
    public FloatData RenderDepth()
    {
        GetCameraRays(out var ro, out var rd);
        using (ro)
        using (rd) {
            return RenderDepth(ro, rd);
        }
    }

    /// <summary>
    /// Returns a depth for every ray.
    /// </summary>
    FloatData RenderDepth(Vec3Data ro, Vec3Data rd)
    {
        var depth = Float(ZNear - 0.1f);
        for (int i = 0; i < DepthIterations; i++) {
            using var dp = rd*depth;
            dp.AddInplace(ro);
            using var d = Map(dp);
            depth.AddInplace(d);
        }
        return depth;
    }

    void GetCameraRays(out Vec3Data ro, out Vec3Data rd)
    {
        using var uv = NewVec2();
        var uvv = uv.Values;
        var aspect = (float)width / height;
        var vheight = 2.0f;
        var vwidth = aspect * vheight;
        var dx = vwidth / (width - 1);
        var dy = -vheight / (height - 1);
        var startx = -vwidth * 0.5f;
        var starty = vheight * 0.5f;
        var i = 0;
        for (var yi = 0; yi < height; ++yi)
        {
            var y = starty + yi * dy;
            for (var xi = 0; xi < width; ++xi)
            {
                uvv[i++] = startx + xi * dx;
                uvv[i++] = y;
            }
        }
        ro = Vec3(0, 0, 5);
        using var nearPlane = Vec3(uv, -ZNear);
        rd = Normalize(nearPlane);
    }

    /// <summary>
    /// Returns a color for every pixel in fragCoord.
    /// </summary>
    Vec3Data Render(Vec3Data ro, Vec3Data rd)
    {
        using var t = Float(ZNear - 0.1f);
        for (int i = 0; i < DepthIterations; i++) {
            using var dp = rd*t;
            dp.AddInplace(ro);
            using var d = Map(dp);
            t.AddInplace(d);
        }
        var rp = ro + rd*t;
        var n = DistGrad(rp).NormalizeInplace();
        using var bgmask = t > 9.0f;
        using var bg = bgmask * new Vector3(0.5f, 0.75f, 1.0f);
        var diff = new Vector3(1.0f, 0.5f, 0.25f);
        bgmask.NotInplace();
        using var fg = bgmask * diff;
        var fragColor = fg + bg;
        return fragColor;
    }

    const float GradOffset = 1e-4f;

    static readonly Vector3[] GradOffsets = {
        GradOffset * Vector3.UnitX,
        GradOffset * Vector3.UnitY,
        GradOffset * Vector3.UnitZ,
        -GradOffset * Vector3.UnitX,
        -GradOffset * Vector3.UnitY,
        -GradOffset * Vector3.UnitZ,
    };

    Vec3Data DistGrad(Vec3Data p)
    {
        FloatData? px = null, py = null, pz = null;
        FloatData? nx = null, ny = null, nz = null;
        
        Parallel.ForEach(GradOffsets, (o, s, i) => {
            // Console.WriteLine($"{i} = {o}");
            using var op = p + o;
            var d = Map(op);
            switch (i) {
                case 0: px = d; break;
                case 1: py = d; break;
                case 2: pz = d; break;
                case 3: nx = d; break;
                case 4: ny = d; break;
                case 5: nz = d; break;
            }
        });
        if (px is null || py is null || pz is null) {
            throw new System.Exception("px, py, pz are null");
        }
        if (nx is null || ny is null || nz is null) {
            throw new System.Exception("nx, ny, nz are null");
        }
        using (px) using (py) using (pz)
        using (nx) using (ny) using (nz) {
            using var pp = Vec3(px, py, pz);
            using var np = Vec3(nx, ny, nz);
            return pp - np;
        }
        throw new NotImplementedException ();
    }

    FloatData Map(Vec3Data p)
    {
        var distances = NewFloat();
        sdf.Sample(p.Vector3Memory, distances.FloatMemory, batchSize, maxDegreeOfParallelism);
        return distances;
    }

    FloatData NewFloat() => new FloatData(width, height, pool);
    FloatData Float(float x)
    {
        var data = NewFloat();
        Array.Fill(data.Values, x);
        return data;
    }
    Vec2Data NewVec2() => new Vec2Data(width, height, pool);
    Vec3Data NewVec3() => new Vec3Data(width, height, pool);
    Vec3Data Vec3(float x, float y, float z)
    {
        var data = NewVec3();
        var v = data.Values;
        var n = data.Length;
        for (int i = 0; i < n; ) {
            v[i++] = x;
            v[i++] = y;
            v[i++] = z;
        }
        return data;
    }
    Vec3Data Vec3(Vec2Data xy, float z)
    {
        var data = NewVec3();
        var v = data.Values;
        var bv = xy.Values;
        var n = data.Length;
        for (int i = 0, j = 0; i < n; ) {
            var x = bv[j++];
            var y = bv[j++];
            v[i++] = x;
            v[i++] = y;
            v[i++] = z;
        }
        return data;
    }
    Vec3Data Vec3(FloatData x, FloatData y, FloatData z)
    {
        var data = NewVec3();
        var v = data.Values;
        var bx = x.Values;
        var by = y.Values;
        var bz = z.Values;
        var n = data.Length;
        for (int i = 0, j = 0; i < n; ) {
            v[i++] = bx[j];
            v[i++] = by[j];
            v[i++] = bz[j];
            j++;
        }
        return data;
    }
}
