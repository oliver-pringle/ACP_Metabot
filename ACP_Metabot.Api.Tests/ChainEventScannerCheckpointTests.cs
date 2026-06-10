using ACP_Metabot.Api.Services;
using Xunit;

namespace ACP_Metabot.Api.Tests;

// Locks in the checkpoint policy that pairs with the cumulative-total fold
// (2026-06-10). A trustworthy scan advances last_scanned_block to the head it
// reached; an untrustworthy one (step-1 dropped chunks => possible undercount)
// must NOT advance past the data it missed — it leaves the checkpoint exactly one
// below the original fromBlock so the next pass re-scans the identical window.
// This is what stops a partial scan from freezing a wrong count permanently.
public class ChainEventScannerCheckpointTests
{
    [Fact]
    public void Trustworthy_scan_advances_checkpoint_to_head()
    {
        Assert.Equal(46_840_000,
            ChainEventScanner.ChooseHighestScannedBlock(head: 46_840_000, fromBlock: 46_800_000, step1Trustworthy: true));
    }

    [Fact]
    public void Untrustworthy_delta_does_not_advance_keeps_prior_checkpoint()
    {
        // Delta scan from block 46_829_044 (= prior checkpoint 46_829_043 + 1) that
        // dropped chunks -> leave the checkpoint at 46_829_043 so the next pass
        // re-scans [46_829_044..head] rather than skipping past the missed blocks.
        Assert.Equal(46_829_043,
            ChainEventScanner.ChooseHighestScannedBlock(head: 46_840_000, fromBlock: 46_829_044, step1Trustworthy: false));
    }

    [Fact]
    public void Untrustworthy_firstever_coldstart_resets_to_zero_for_retry()
    {
        // First-ever scan (fromBlock = 1) that was untrustworthy -> checkpoint 0 so
        // GetLastScannedBlockAsync(=0)+1 = 1 re-triggers a full cold-start next pass.
        Assert.Equal(0,
            ChainEventScanner.ChooseHighestScannedBlock(head: 46_840_000, fromBlock: 1, step1Trustworthy: false));
    }
}
