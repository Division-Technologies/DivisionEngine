using System.Numerics;

namespace DivisionEngine.Tests.Transforms;

/// <summary>
///     <see cref="LocalTransform.ToMatrix" /> builds its matrix by hand instead of composing three
///     matrices, for speed. These tests hold it to the composed form it replaced, so the saving
///     cannot quietly become a behaviour change.
/// </summary>
[TestFixture]
public sealed class TransformMathTests
{
    /// <summary>
    ///     The two forms agreed bit for bit when measured, but the comparison is made with a
    ///     tolerance: both sides are ordinary floating-point arithmetic, and nothing guarantees an
    ///     identical rounding order on every instruction set. A wrong derivation is off by far more
    ///     than this.
    /// </summary>
    private const float Tolerance = 1e-6f;

    private static Matrix4x4 Composed(in LocalTransform t)
    {
        return Matrix4x4.CreateScale(t.Scale)
               * Matrix4x4.CreateFromQuaternion(t.Rotation)
               * Matrix4x4.CreateTranslation(t.Position);
    }

    private static IEnumerable<LocalTransform> Cases()
    {
        yield return LocalTransform.Identity;
        yield return LocalTransform.FromPosition(new Vector3(1, -2, 3));
        yield return LocalTransform.Identity with { Scale = new Vector3(2, 3, 4) };
        yield return LocalTransform.Identity with { Scale = new Vector3(-1, 1, 1) }; // mirrored
        yield return LocalTransform.Identity with { Scale = Vector3.Zero };
        yield return LocalTransform.FromRotation(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 3));

        // A rotation about each axis in turn, so an error in any one row shows up.
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            yield return new LocalTransform
            {
                Position = new Vector3(5, 6, 7),
                Rotation = Quaternion.CreateFromAxisAngle(axis, 1.1f),
                Scale = new Vector3(1.5f, 2.5f, 0.5f)
            };
        }

        var rng = new Random(20260921);
        for (var i = 0; i < 200; i++)
        {
            yield return new LocalTransform
            {
                Position = new Vector3(Next(rng), Next(rng), Next(rng)),
                Rotation = Quaternion.Normalize(new Quaternion(Next(rng), Next(rng), Next(rng), Next(rng))),
                Scale = new Vector3(Next(rng), Next(rng), Next(rng))
            };
        }

        static float Next(Random rng)
        {
            return (float)(rng.NextDouble() * 4 - 2);
        }
    }

    [Test]
    public void ToMatrix_MatchesTheComposedForm()
    {
        foreach (var transform in Cases())
        {
            var actual = transform.ToMatrix();
            var expected = Composed(transform);

            Assert.That(MaxAbsoluteDifference(actual, expected), Is.LessThanOrEqualTo(Tolerance),
                $"ToMatrix disagrees with CreateScale * CreateFromQuaternion * CreateTranslation for {transform.Position}, {transform.Rotation}, {transform.Scale}");
        }
    }

    /// <summary>
    ///     Spot-checks the convention rather than the arithmetic: a point goes through scale, then
    ///     rotation, then translation, and <see cref="Matrix4x4" /> is row-vector.
    /// </summary>
    [Test]
    public void ToMatrix_AppliesScaleThenRotationThenTranslation()
    {
        var transform = new LocalTransform
        {
            Position = new Vector3(10, 0, 0),
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2),
            Scale = new Vector3(2, 2, 2)
        };

        // (1,0,0) scaled to (2,0,0), turned a quarter turn about Z to (0,2,0), then moved by X+10.
        var point = Vector3.Transform(Vector3.UnitX, transform.ToMatrix());

        Assert.Multiple(() =>
        {
            Assert.That(point.X, Is.EqualTo(10f).Within(1e-5f));
            Assert.That(point.Y, Is.EqualTo(2f).Within(1e-5f));
            Assert.That(point.Z, Is.EqualTo(0f).Within(1e-5f));
        });
    }

    [Test]
    public void ToMatrix_OfIdentity_IsIdentity()
    {
        Assert.That(LocalTransform.Identity.ToMatrix(), Is.EqualTo(Matrix4x4.Identity));
    }

    private static float MaxAbsoluteDifference(in Matrix4x4 a, in Matrix4x4 b)
    {
        var worst = 0f;
        for (var row = 0; row < 4; row++)
        {
            for (var column = 0; column < 4; column++)
            {
                worst = MathF.Max(worst, MathF.Abs(a[row, column] - b[row, column]));
            }
        }

        return worst;
    }
}