using Xunit;

// Run this assembly's tests one at a time.
//
// Two reasons, both about shared state that no individual test owns:
//
// 1. MongoDB's serialization configuration is process-global (conventions, class maps, the Guid
//    serializer). TestAssemblySetup registers it deterministically before any test, but class maps are
//    still frozen on first use per type, so tests that touch the same entity types must not interleave.
//
// 2. NSubstitute queues argument matchers in a per-thread context. These tests are async and use
//    Arg.Any<> matchers inside Received(...) verification, so a continuation resuming on a different
//    thread while another test is configuring a substitute is a documented hazard.
//
// The cost is nothing measurable: the whole assembly runs in roughly a quarter of a second. The
// benefit is that a green run means the code is correct rather than that the scheduling was lucky.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
