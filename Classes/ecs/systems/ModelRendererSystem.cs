using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using sage_engine;

class ModelRendererSystem : ComponentSystem{
    private EntityContext context = EntityContext.getInstance();
    private static ModelRendererSystem instance = null;

    List<Entity> entities;


    private ModelRendererSystem()
    {
        entities = new List<Entity>();
        entities = context.getAllEntities(["TransformComponent", "ModelComponent"]);
    }

    public static ModelRendererSystem getInstance()
    {
        if (instance == null)
        {
            instance = new ModelRendererSystem();
        }
        return instance;
    }

    public void update()
    {
       
    }

    public void render(Camera camera){
        foreach (Entity entity in entities){
            foreach (ModelMesh mesh in entity.getComponent<ModelComponent>().model.Meshes)
            {
                foreach (BasicEffect effect in mesh.Effects)
                {   
                    Vector3 enitityPosition = entity.getComponent<TransformComponent>().position;
                    Quaternion entityRotation = entity.getComponent<TransformComponent>().rotation;

                    Matrix worldRotationMatrix = Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(entityRotation.Y), MathHelper.ToRadians(entityRotation.X), MathHelper.ToRadians(entityRotation.Z));
                    Matrix worldPositionMatrix = Matrix.CreateTranslation(enitityPosition.X, enitityPosition.Y, enitityPosition.Z);

                    effect.View = camera.getViewMatrix(); // main_camera.viewMatrix
                    effect.World = Matrix.Identity * worldPositionMatrix; // main_camera.worldMatrix
                    effect.Projection = camera.getProjectionMatrix(); // main_camera.projectionMatrix

                    Vector3 lightDirection = new Vector3(0, -20, 0);
                    lightDirection.Normalize();

                    effect.DirectionalLight0.Direction = lightDirection;
                    effect.DirectionalLight0.DiffuseColor = new Vector3(1, 1, 1);
                    effect.DirectionalLight0.SpecularColor = new Vector3(1, 1, 1);
                    //effect.DirectionalLight0.Enabled = true;
                    effect.AmbientLightColor = new Vector3(0.6f, 0.6f, 0.6f);
                    effect.EnableDefaultLighting();


                    mesh.Draw();
                }
            }
        }
    }
}