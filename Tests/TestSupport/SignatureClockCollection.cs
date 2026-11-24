namespace Flower.Tests.TestSupport;

// Clock-skew refusals temporarily correct the process-wide signing clock.
// Every DeviceSigningKey reads it, including keys in otherwise unrelated
// tests, so the tests that correct it must run without other collections.
// Their data-directory overrides are isolated by the same exclusion.
[CollectionDefinition("SignatureClock", DisableParallelization = true)]
public sealed class SignatureClockCollection
{
}
