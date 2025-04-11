using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Liru3D.Models;
using Liru3D.Animations;
namespace sage_engine;

class SkinnedMesh_Entity : Entity{
    public String name {get;}
    public SkinnedModel model {get;}
    public Vector3 position {get; set;}
    public Vector3 rotation {get; set;}

    public AnimationPlayer animationPlayer;
    
    //overloaded constructor
    
    public SkinnedMesh_Entity(String param_name, SkinnedModel param_model, Vector3 param_position, Vector3 param_rotaiton) : base(param_name, param_model, param_position, param_rotaiton){
        this.name = param_name;
        this.model = param_model;
        this.position = param_position;
        this.rotation = param_rotaiton;
        this.animationPlayer = new AnimationPlayer(model);
        this.animationPlayer.Animation = model.Animations[0];
        
    }

    public void setPosition(Vector3 param_position){
        this.position = param_position;
    }

    public Vector3 getPosition(){
        return this.position;
    }
}