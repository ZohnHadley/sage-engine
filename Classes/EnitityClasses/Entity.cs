using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Liru3D.Models;
using Liru3D.Animations;
namespace sage_engine;

class Entity{
    public String name {get;}

    public AnimationPlayer animationPlayer;
    public SkinnedModel skinned_model {get;}

    public Model model {get;}

    public Vector3 position {get; set;}
    public Vector3 rotation {get; set;}

    
    public Entity(String param_name, SkinnedModel param_model, Vector3 param_position, Vector3 param_rotaiton){
        this.name = param_name;
        this.skinned_model = param_model;
        this.position = param_position;
        this.rotation = param_rotaiton;
        this.animationPlayer = new AnimationPlayer(skinned_model);
        this.animationPlayer.Animation = skinned_model.Animations[0];
        
    }

    public Entity(String param_name, Model param_model, Vector3 param_position, Vector3 param_rotaiton){
        this.name = param_name;
        this.model = param_model;
        this.position = param_position;
        this.rotation = param_rotaiton;
    }

    public void setPosition(Vector3 param_position){
        this.position = param_position;
    }

    public Vector3 getPosition(){
        return this.position;
    }
}