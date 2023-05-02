using IceRpc.Features;
using IceRpc.Slice;

namespace GreeterExample;

/// <summary>Implements the IGreeterService interface generated by the Slice compiler.</summary>
internal class Chatbot : Service, IGreeterService
{
    public ValueTask<string> GreetAsync(string name, IFeatureCollection features, CancellationToken cancellationToken)
    {
        Console.WriteLine($"{name} says hello!");
        return new($"Hello, {name}!");
    }
}
