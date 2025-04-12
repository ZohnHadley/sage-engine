using Microsoft.Xna.Framework;

class TransformComponent : Component
{
    public Vector3 position { get; set; }
    public Quaternion rotation { get; set; }
    public Vector3 scale { get; set; }
 
}