using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;
internal class ModelRendererSystem : IComponentSystem{
    private static ModelRendererSystem instance = null;

    // Live set of entities that have both a transform and a mesh renderer, maintained
    // incrementally by the entity-context system instead of rescanned every frame.
    private readonly ArchetypeView _renderables;

    private ModelRendererSystem()
    {
        _renderables = EntityContextListener.getInstance().Track("ComponentTransform", "ComponentMeshRenderer");
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

    public void render(DevCamera camera){
        if (_renderables.Entities.Count == 0)
        {
            return;
        }
        foreach (Entity entity in _renderables.Entities){
            foreach (ModelMesh mesh in entity.getComponent<ComponentMeshRenderer>().model.Meshes)
            {
                foreach (BasicEffect effect in mesh.Effects)
                {   
                    ComponentTransform transform = entity.getComponent<ComponentTransform>();
                    Vector3 entityPosition = transform.Position;
                    Quaternion entityRotation = transform.Rotation;

                    Matrix worldPositionMatrix = Matrix.CreateTranslation(entityPosition.X, entityPosition.Y, entityPosition.Z);

                    effect.View = camera.getViewMatrix(); // main_camera.viewMatrix
                    effect.World = Matrix.CreateFromQuaternion(entityRotation) * worldPositionMatrix;
                    effect.Projection = camera.getProjectionMatrix(); // main_camera.projectionMatrix
                      
                    Vector3 lightDirection = new Vector3(0, -20, 0);
                    lightDirection.Normalize(); 
                    
                    effect.EnableDefaultLighting();


                    mesh.Draw();
                }
            }
        }
    }
}