using Xunit;

// Run this assembly's tests one at a time.
//
// This suite combines every ingredient of an order-dependent failure: a shared real MongoDB, the
// process-global MongoDB serialization configuration (registered incidentally by whichever test calls
// AddFoundryMongo first), and NSubstitute matchers queued per thread across async continuations.
//
// It has already produced one confirmed bug of that shape, where FullFlow_WithRealMongoDB passed or
// failed according to whether another test's AddFoundryMongo call had registered the camelCase
// convention pack first -- that is, according to xUnit's scheduling rather than anything in the code.
//
// Serialising removes the entire class of nondeterminism. The cost was measured, not assumed.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
