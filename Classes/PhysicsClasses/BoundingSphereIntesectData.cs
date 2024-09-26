class BoundingSphereIntesectData{
    private bool doesIntersect;
    private float distance;

    public BoundingSphereIntesectData(bool doesIntersect, float distance){
        this.doesIntersect = doesIntersect;
        this.distance = distance;
    }

    public bool getDoesIntersect(){
        return doesIntersect;
    }

    public float getDistance(){
        return distance;
    }
}