using Microsoft.Xna.Framework;

class BoundingSphere
{
    public Vector3 Center;
    public float Radius;
    private BoundingSphereIntesectData intersectData;

    public BoundingSphere(Vector3 center, float radius)
    {
        Center = center;
        Radius = radius;
    }

    public BoundingSphereIntesectData Intersects(BoundingSphere other)
    {
        float distance = Vector3.Distance(Center, other.Center);
        float sumRadii = Radius + other.Radius;
        bool doesIntersect = distance < sumRadii;
        
        intersectData = new BoundingSphereIntesectData(doesIntersect, distance);
        return intersectData;
    }
}
 