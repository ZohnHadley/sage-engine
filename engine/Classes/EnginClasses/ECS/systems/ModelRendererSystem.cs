using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;
internal class ModelRendererSystem : IComponentSystem{
    private static ModelRendererSystem instance = null;
    private EntityContext context;

    private List<Entity> entities;

    private ModelRendererSystem()
    {
        context = EntityContext.getInstance();
        entities = new List<Entity>();
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

    public void update(GameTime deltaTime)
    {
       
    }

    public void render(Camera camera){
        entities = context.getAllEntitiesWithListOfComponents(["ComponentTransform", "ComponentMeshRenderer"]);
        if (entities.Count == 0)
        {
            return;
        }
        foreach (Entity entity in entities){
            foreach (ModelMesh mesh in entity.getComponent<ComponentMeshRenderer>().model.Meshes)
            {
                foreach (BasicEffect effect in mesh.Effects)
                {   
                    ComponentTransform transform = entity.getComponent<ComponentTransform>();
                    Vector3 entityPosition = transform.position;
                    Quaternion entityRotation = transform.rotation;

                    Matrix worldPositionMatrix = Matrix.CreateTranslation(entityPosition.X, entityPosition.Y, entityPosition.Z);

                    effect.View = camera.getViewMatrix(); // main_camera.viewMatrix
                    effect.World = Matrix.CreateFromQuaternion(entityRotation) * worldPositionMatrix;
                    effect.Projection = camera.getProjectionMatrix(); // main_camera.projectionMatrix
                      
                    Vector3 lightDirection = new Vector3(0, -20, 0);
                    lightDirection.Normalize();

                    // effect.DirectionalLight0.Enabled = true;
                    // effect.DirectionalLight0.Direction = lightDirection;
                    // effect.DirectionalLight0.DiffuseColor = new Vector3(0.15f, 0.15f, 0.15f);
                    // effect.DirectionalLight0.SpecularColor = new Vector3(0.1f,0.1f, 0.1f);
                    // effect.AmbientLightColor = new Vector3(0.26f, 0.26f, 0.26f);
                    effect.EnableDefaultLighting();


                    mesh.Draw();
                }
            }
        }
    }
}