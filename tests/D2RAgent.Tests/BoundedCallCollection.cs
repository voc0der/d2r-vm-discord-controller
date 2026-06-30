using Xunit;

namespace D2RAgent.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BoundedCallCollection
{
    public const string Name = "Bounded call semaphore";
}
