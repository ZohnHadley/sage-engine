using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;
class ModelRendererSystem : ComponentSystem{
    private static ModelRendererSystem instance = null;
    private EntityContext context;

    private List<Entity> entities;


    private ModelRendererSystem()
    {
        context = EntityContext.getInstance();
        entities = new List<Entity>();
        entities = context.getAllEntitiesWithListOfComponents(["ComponentTransform", "ComponentMeshRenderer"]);
        //list of all entities with model component and transform component
         
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
        if (entities.Count == 0)
        {
            return;
        }
        foreach (Entity entity in entities){
            foreach (ModelMesh mesh in entity.getComponent<ComponentMeshRenderer>().model.Meshes)
            {
                foreach (BasicEffect effect in mesh.Effects)
                {   
                    Vector3 enitityPosition = entity.getPosition();
                    Quaternion entityRotation = entity.getRotation();

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
                    effect.AmbientLightColor = new Vector3(0.26f, 0.26f, 0.26f);
                    effect.EnableDefaultLighting();


                    mesh.Draw();
                }
            }
        }
    }
}