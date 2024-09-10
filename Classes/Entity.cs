using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
namespace sage_engine;

class Entity{
    public String name {get;}
    public Model model {get;}
    public Vector3 position {get; set;}
    public Vector3 rotation {get; set;}
    
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