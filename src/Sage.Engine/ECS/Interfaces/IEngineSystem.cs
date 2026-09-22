namespace sage_engine;

// Engine-side systems update with a plain delta in seconds, so Sage.Engine doesn't depend on MonoGame's GameTime.
internal interface IEngineSystem
{
    public void update(float deltaSeconds);
}