using Xunit;

// VmOperations.BoundedCallSlots is a process-global semaphore, and TryRunBoundedTests asserts
// against its absolute state - "the cap is saturated", "my slot came back". No collection
// attribute can protect that from tests in *other* collections running at the same time, and the
// evidence says the [CollectionDefinition(DisableParallelization = true)] on BoundedCallCollection
// was not doing so: run 34369302513 entered that test with all 32 slots already held by something
// outside it, having earlier failed the same test at 25, 27 and 28 of 32.
//
// Four attempts to make the assertion resilient each fixed a real defect and uncovered the next -
// a timeout that was too short, a cascade across tests, a race between measuring free capacity and
// probing it. All of them were working around the same thing: a shared mutable resource being read
// while other tests were free to use it. Serialising the assembly removes the interference instead
// of accommodating it. The suite is ~1500 fast tests and this costs CI under a minute, which is
// cheaper than one more red main.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
