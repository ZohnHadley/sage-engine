using System.Collections.Generic;
using Liru3D.Models;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
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
    /*public void renderEntities()
    {
        foreach (Entity entity in entities)
        {
            bool isEntitySkinned = entity is SkinnedMesh_Entity;
            Matrix worldRotationMatrix = Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(entity.rotation.Y), MathHelper.ToRadians(entity.rotation.X), MathHelper.ToRadians(entity.rotation.Z));
            Matrix worldPositionMatrix = Matrix.CreateTranslation(entity.position.X, entity.position.Y, entity.position.Z);

            if (isEntitySkinned)
            {
                SkinnedMesh_Entity ent = (SkinnedMesh_Entity)entity;
                skinned_effect.View = main_camera.viewMatrix;
                skinned_effect.World = Matrix.Identity; // main_camera.worldMatrix
                skinned_effect.Projection = main_camera.projectionMatrix;


                skinned_effect.EnableDefaultLighting(); // enable default lighting
                skinned_effect.PreferPerPixelLighting = true;
                skinned_effect.SpecularColor = new Vector3(1f);
                skinned_effect.SpecularPower = 10f;
                skinned_effect.AmbientLightColor = new Vector3(0, 1, 0);
                skinned_effect.CurrentTechnique.Passes[0].Apply();

                //ent.animationPlayer.SetEffectBones(skinned_effect);

                foreach (SkinnedMesh mesh in ent.model.Meshes)
                {
                    mesh.Draw();
                }
               
            }
           
            
        }
    }*/

    public void renderEntities()
    {
        foreach (Entity entity in entities)
        {
            foreach (ModelMesh mesh in entity.model.Meshes)
            {

                foreach (BasicEffect effect in mesh.Effects)
                {
                    Matrix worldRotationMatrix = Matrix.CreateFromYawPitchRoll(MathHelper.ToRadians(entity.rotation.Y), MathHelper.ToRadians(entity.rotation.X), MathHelper.ToRadians(entity.rotation.Z));
                    Matrix worldPositionMatrix = Matrix.CreateTranslation(entity.position.X, entity.position.Y, entity.position.Z);

                    effect.View = main_camera.viewMatrix;
                    effect.World = Matrix.Identity * worldRotationMatrix * worldPositionMatrix; // main_camera.worldMatrix
                    effect.Projection = main_camera.projectionMatrix;
                    
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