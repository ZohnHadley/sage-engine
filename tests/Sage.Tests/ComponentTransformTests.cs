using System.Numerics;
using sage_engine;

namespace sage_engine.Tests;

// Guards the LookAt/Billboard fixes (review items #30/#31) and their port to System.Numerics.
public class ComponentTransformTests
{
    private static readonly Vector3 Forward = -Vector3.UnitZ;   // local forward, MonoGame/System.Numerics convention
    private const float Tolerance = 1e-4f;

    private static Vector3 FacingOf(ComponentTransform t) => Vector3.Transform(Forward, t.Rotation);

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < Tolerance, $"expected {expected}, got {actual}");
    }

    [Fact]
    public void Defaults_AreIdentityPose()
    {
        var t = new ComponentTransform();
        Assert.Equal(Vector3.Zero, t.Position);
        Assert.Equal(Quaternion.Identity, t.Rotation);
        Assert.Equal(Vector3.One, t.Scale);
    }

    [Theory]
    [InlineData(0, 0, -5)]    // straight ahead: stays identity
    [InlineData(5, 0, 0)]     // to the right
    [InlineData(-3, 2, 4)]    // behind, up and to the left
    [InlineData(0, 10, 0)]    // straight up (degenerate up vector)
    [InlineData(0, -10, 0)]   // straight down
    public void LookAt_PointsLocalForwardAtTarget(float x, float y, float z)
    {
        var t = new ComponentTransform { Position = new Vector3(1, 2, 3) };
        var target = t.Position + new Vector3(x, y, z);

        t.LookAt(target);

        AssertClose(Vector3.Normalize(target - t.Position), FacingOf(t));
    }

    [Theory]
    [InlineData(0, 0, -5)]
    [InlineData(5, 0, 0)]
    [InlineData(-3, 2, 4)]
    [InlineData(0, 10, 0)]
    public void Billboard_PointsLocalForwardAtTarget(float x, float y, float z)
    {
        var t = new ComponentTransform { Position = new Vector3(-2, 0, 7) };
        var camera = t.Position + new Vector3(x, y, z);

        t.Billboard(camera);

        AssertClose(Vector3.Normalize(camera - t.Position), FacingOf(t));
    }

    [Fact]
    public void LookAt_TargetOnTopOfObject_KeepsRotation()
    {
        var t = new ComponentTransform { Position = new Vector3(4, 5, 6) };
        t.LookAt(new Vector3(10, 5, 6));
        var before = t.Rotation;

        t.LookAt(t.Position);
        t.Billboard(t.Position);

        Assert.Equal(before, t.Rotation);
    }

    [Fact]
    public void LookAt_NeverProducesNaN()
    {
        var t = new ComponentTransform();
        t.LookAt(new Vector3(0, 1, 0));
        Assert.False(float.IsNaN(t.Rotation.X) || float.IsNaN(t.Rotation.Y) || float.IsNaN(t.Rotation.Z) || float.IsNaN(t.Rotation.W));
    }
}
