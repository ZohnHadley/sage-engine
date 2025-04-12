using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
namespace sage_engine;

class Entity{
    private String name {get;}
    private Model model {get;}
    private Vector3 position {get; set;}
    private Vector3 rotation {get; set;}
  
    public Entity(String _name, Model _model, Vector3 _position){
        this.name = _name;
        this.model = _model;
        this.position = _position;
    }
    
    public String getName(){
        return this.name;
    }
    
    public Model getModel(){
        return this.model;
    }
    
    public Vector3 getRotation(){
        return this.rotation;
    }
    
    public Vector3 getPosition(){
        return this.position;
    }

    public void setRotation(Vector3 _rotation){
        this.rotation = _rotation;
    }

    public void setPosition(Vector3 _position){
        this.position = _position;
    }
}