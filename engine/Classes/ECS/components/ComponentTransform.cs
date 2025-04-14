using Microsoft.Xna.Framework;

namespace sage_engine;
class ComponentTransform : Component
{
    public Vector3 position { get; set; }
    public Quaternion rotation { get; set; }
    public Vector3 scale { get; set; }
 
}