using Xunit;

// Several tests set process environment variables to exercise the path a deployed pod actually
// uses — the un-prefixed environment-variable provider folding Foundry__ApiKey and the retired
// DocumentIntelligence__*/AzureOpenAI__* names into IConfiguration. The process environment is
// global, and almost every test class in this assembly builds a host that reads it, so with
// collections running in parallel one class's variable lands in another class's configuration.
//
// That was latent before and is now fatal by design: a retired key is meant to fail a boot, so a
// leaked one fails whichever unrelated host happens to be building at that moment. Serialising the
// assembly is the honest fix — the suite runs in about two seconds either way, and the alternative
// is every host-building class sharing one collection, which is the same thing spelled longer.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
