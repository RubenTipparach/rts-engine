namespace RtsEngine.Core;

/// <summary>
/// Abstraction over the draw call.
/// Platform-agnostic draw contract.
/// Implementations receive MVP matrix per frame.
/// </summary>
public interface IRenderer
{
    void Draw(float[] mvpRawFloats);
}
