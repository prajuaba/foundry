using Xunit;

// Run this assembly's tests one at a time.
//
// These tests use NSubstitute, which queues argument matchers in a per-thread context, and they are
// async -- so a continuation resuming on a different thread while another test configures a substitute
// is a documented hazard. They also share the process-global MongoDB serialization configuration.
//
// Applied for the same reason as in FoundryMongo.Tests and Foundry.IntegrationTests: a green run should
// mean the code is correct, not that the scheduling was lucky.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
