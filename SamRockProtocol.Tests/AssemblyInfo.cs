using Xunit;

// xunit by default runs different test classes in parallel. The integration
// test (SamRockProtocolHappyPathTest) boots a full BTCPay ServerTester via
// SharedPluginTestFixture which binds to a fixed port. Running unit-test
// classes in parallel with that boot can confuse fixture-init ordering and
// MVC ApplicationParts discovery on the running server. Disable parallelism
// so the integration test runs in isolation after the (fast) unit tests.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
