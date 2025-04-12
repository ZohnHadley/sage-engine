using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
namespace sage_engine;
class EntityManager
{
    private Camera main_camera;
    private List<Entity> entities;
    SkinnedEffect skinned_effect;
    public EntityManager(Camera param_main_camera, GraphicsDevice param_graphics_device)
    {
        main_camera = param_main_camera;
        entities = new List<Entity>();
        skinned_effect = new SkinnedEffect(param_graphics_device);
    }

    public void addEntity(Entity param_entity)
    {
        entities.Add(param_entity);
    } 

    public void renderEntities()
    {
        foreach (Entity entity in entities)
        {
            foreach (ModelMesh mesh in entity.getModel().Meshes)
            {

                foreach (BasicEffect effect in mesh.Effects)
                {
                    Matrix worldRotationMatrix = Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(entity.getRotation().Y), MathHelper.ToRadians(entity.getRotation().X), MathHelper.ToRadians(entity.getRotation().Z));
                    Matrix worldPositionMatrix = Matrix.CreateTranslation(entity.getPosition().X, entity.getPosition().Y, entity.getPosition().Z);

                    effect.View = main_camera.getViewMatrix(); // main_camera.viewMatrix
                    effect.World = Matrix.Identity * worldPositionMatrix; // main_camera.worldMatrix
                    effect.Projection = main_camera.getProjectionMatrix(); // main_camera.projectionMatrix
                    
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