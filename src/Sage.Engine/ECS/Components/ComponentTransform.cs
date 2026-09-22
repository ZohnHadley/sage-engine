using System;
using System.Numerics;

namespace sage_engine;
internal class ComponentTransform : IComponent
{
  private Vector3 _position = Vector3.Zero;
  private Quaternion _rotation = Quaternion.Identity;
  private Vector3 _scale = Vector3.One;

  public Vector3 Position {
    get
    {
      return _position;
    }

    set
    {
      _position = value;  
    } 
  }
  public Quaternion Rotation {
    get
    {
      return _rotation;    
    }
    set
    {
      _rotation = value;
    }
  }
  public Vector3 Scale {
    get
    {
      return _scale;
    }
    set
    {
      _scale = value;
    } 
  }
 
 
  // Rotates so the local Forward (-Z) axis points at the target.
  public void LookAt(Vector3 targetPosition)
  {
    Vector3 direction = targetPosition - Position;
    if (direction.LengthSquared() < MinDirectionLengthSquared)
      return; // target is on top of us: no defined direction, keep the current rotation
    direction = Vector3.Normalize(direction);

    // CreateWorld (not CreateLookAt, which builds the inverse view matrix) maps Forward onto direction.
    Matrix4x4 world = Matrix4x4.CreateWorld(Vector3.Zero, direction, UpHintFor(direction));
    Rotation = Quaternion.CreateFromRotationMatrix(world);
  }

  // Spherical billboard: rotates so the local Forward (-Z) axis faces the target (e.g. the camera).
  public void Billboard(Vector3 targetPosition)
  {
    Vector3 direction = targetPosition - Position;
    if (direction.LengthSquared() < MinDirectionLengthSquared)
      return;

    // CreateBillboard(objectPosition, cameraPosition, ...) — object first, target second.
    Matrix4x4 billboard = Matrix4x4.CreateBillboard(Position, targetPosition, UpHintFor(direction), Forward);
    Rotation = Quaternion.CreateFromRotationMatrix(billboard);
  }

  private const float MinDirectionLengthSquared = 1e-8f;

  // System.Numerics has no Up/Forward constants; same convention as MonoGame (right-handed, forward = -Z).
  private static readonly Vector3 Up = Vector3.UnitY;
  private static readonly Vector3 Forward = -Vector3.UnitZ;

  // Up is parallel to a straight-up/down direction, which makes the cross products
  // inside CreateWorld/CreateBillboard collapse to zero (NaN rotation). Swap the hint there.
  private static Vector3 UpHintFor(Vector3 direction)
  {
    float alignment = Vector3.Dot(Vector3.Normalize(direction), Up);
    return MathF.Abs(alignment) > 0.999f ? Forward : Up;
  }

}