using System.Numerics;

namespace Sage.Tests;

// The engine has its own `Assert` (Assert.Dev/Ensure/Check); inside this namespace it would hide xUnit's.
using Assert = Xunit.Assert;

// Guards the LookAt/Billboard fixes (review items #30/#31), their port to System.Numerics, and the
// Transform struct's identity default (review item #3).
public class TransformTests
{
    private const float Tolerance = 1e-4f;

    private static Vector3 FacingOf(in Transform t) => Vector3.Transform(TransformMath.Forward, t.LocalRotation);

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < Tolerance, $"expected {expected}, got {actual}");
    }

    [Fact]
    public void Identity_IsIdentityPose()
    {
        var t = Transform.Identity;
        Assert.Equal(Vector3.Zero, t.LocalPosition);
        Assert.Equal(Quaternion.Identity, t.LocalRotation);
        Assert.Equal(Vector3.One, t.LocalScale);
        Assert.Equal(Matrix4x4.Identity, t.LocalMatrix);

        var at = Transform.At(new Vector3(1, 2, 3));
        Assert.Equal(Quaternion.Identity, at.LocalRotation);
        Assert.Equal(Vector3.One, at.LocalScale);
    }

    [Fact]
    public void LocalMatrix_AppliesScaleThenRotationThenTranslation()
    {
        var t = Transform.At(new Vector3(10, 0, 0));
        t.LocalScale = new Vector3(2, 2, 2);
        t.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var p = Vector3.Transform(new Vector3(1, 0, 0), t.LocalMatrix);   // scale → (2,0,0), rotate 90° about Y → (0,0,-2), move → (10,0,-2)
        AssertClose(new Vector3(10, 0, -2), p);
    }

    [Theory]
    [InlineData(0, 0, -5)]    // straight ahead: stays identity
    [InlineData(5, 0, 0)]     // to the right
    [InlineData(-3, 2, 4)]    // behind, up and to the left
    [InlineData(0, 10, 0)]    // straight up (degenerate up vector)
    [InlineData(0, -10, 0)]   // straight down
    public void LookAt_PointsLocalForwardAtTarget(float x, float y, float z)
    {
        var t = Transform.At(new Vector3(1, 2, 3));
        var target = t.LocalPosition + new Vector3(x, y, z);

        TransformMath.LookAt(ref t, target);

        AssertClose(Vector3.Normalize(target - t.LocalPosition), FacingOf(t));
    }

    [Theory]
    [InlineData(0, 0, -5)]
    [InlineData(5, 0, 0)]
    [InlineData(-3, 2, 4)]
    [InlineData(0, 10, 0)]
    public void Billboard_PointsLocalForwardAtTarget(float x, float y, float z)
    {
        var t = Transform.At(new Vector3(-2, 0, 7));
        var camera = t.LocalPosition + new Vector3(x, y, z);

        TransformMath.Billboard(ref t, camera);

        AssertClose(Vector3.Normalize(camera - t.LocalPosition), FacingOf(t));
    }

    [Fact]
    public void TargetOnTopOfObject_KeepsRotation_AndNeverNaN()
    {
        var t = Transform.At(new Vector3(4, 5, 6));
        TransformMath.LookAt(ref t, new Vector3(10, 5, 6));
        var before = t.LocalRotation;

        TransformMath.LookAt(ref t, t.LocalPosition);
        TransformMath.Billboard(ref t, t.LocalPosition);

        Assert.Equal(before, t.LocalRotation);
        Assert.False(float.IsNaN(t.LocalRotation.X) || float.IsNaN(t.LocalRotation.W));
    }
}
