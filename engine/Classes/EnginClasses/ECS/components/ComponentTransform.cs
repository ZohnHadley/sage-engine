using Microsoft.Xna.Framework;

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
 
 
  public void LookAt(Vector3 targetPosition)
  {
      Matrix view = Matrix.CreateLookAt(Position, targetPosition, Vector3.Up);
      Rotation = Quaternion.CreateFromRotationMatrix(view);
  }

  public void Billboard(Vector3 targetPosition)
  {
    Matrix view = Matrix.CreateBillboard(targetPosition, Position, Vector3.Up, Vector3.Forward);
    Rotation = Quaternion.CreateFromRotationMatrix(view);
  }

}