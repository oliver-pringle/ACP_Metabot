using ACP_Metabot.Api.Services;
using Nethereum.Contracts;
using Nethereum.Util;
using Xunit;

namespace ACP_Metabot.Api.Tests;

/// <summary>
/// Guards every on-chain event DTO's derived topic0 (event-signature hash) against the
/// canonical ACP v2 ABI shipped by @virtuals-protocol/acp-node-v2 (dist/core/acpAbi.js).
/// A mismatched [Parameter] list makes Nethereum compute the wrong topic0, so the log
/// filter silently matches ZERO logs — the JobCompleted DTO had exactly this bug
/// (missing the indexed `address evaluator`), zeroing completed-job counts portfolio-wide.
/// </summary>
public class ChainEventScannerAbiSignatureTests
{
    private static string Topic0<T>() => ABITypedRegistry.GetEvent<T>().Sha3Signature;
    private static string Canonical(string signature) => new Sha3Keccack().CalculateHash(signature);

    [Fact]
    public void JobCreated_topic0_matches_canonical_abi()
        => Assert.Equal(Canonical("JobCreated(uint256,address,address,address,uint256,address)"), Topic0<JobCreatedEvent>());

    [Fact]
    public void JobFunded_topic0_matches_canonical_abi()
        => Assert.Equal(Canonical("JobFunded(uint256,address,uint256)"), Topic0<JobFundedEvent>());

    [Fact]
    public void JobSubmitted_topic0_matches_canonical_abi()
        => Assert.Equal(Canonical("JobSubmitted(uint256,address,bytes32)"), Topic0<JobSubmittedEvent>());

    [Fact]
    public void JobCompleted_topic0_matches_canonical_abi()
        => Assert.Equal(Canonical("JobCompleted(uint256,address,bytes32)"), Topic0<JobCompletedEvent>());

    [Fact]
    public void JobRejected_topic0_matches_canonical_abi()
        => Assert.Equal(Canonical("JobRejected(uint256,address,bytes32)"), Topic0<JobRejectedEvent>());

    [Fact]
    public void JobExpired_topic0_matches_canonical_abi()
        => Assert.Equal(Canonical("JobExpired(uint256)"), Topic0<JobExpiredEvent>());
}
