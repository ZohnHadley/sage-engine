using Microsoft.Xna.Framework;

namespace sage_engine;
internal class ComponentTransform : IComponent
{
  public Vector3 position { get; set; }
  public Quaternion rotation { get; set; } = Quaternion.Identity;
  public Vector3 scale { get; set; } = Vector3.One;
 
}