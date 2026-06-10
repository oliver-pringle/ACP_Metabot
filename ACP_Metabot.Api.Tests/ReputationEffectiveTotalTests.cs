using ACP_Metabot.Api.Services;
using Xunit;

namespace ACP_Metabot.Api.Tests;

// Locks in the cumulative-total fold that fixes the delta-scan clobber (2026-06-10).
// Before this, ReputationService persisted the per-WINDOW JobCreated count, so every
// daily warmer pass overwrote an agent's accumulated total with just that day's new
// jobs — TheMetaBot read totalJobs=1 having had 12, and high-volume agents that DID
// land a count would have regressed to a few hundred on the next pass. The fold makes
// agentTotalJobs a cumulative running total that a delta scan can only ever advance.
public class ReputationEffectiveTotalTests
{
    [Fact]
    public void FirstEver_coldstart_trustworthy_uses_window_as_baseline()
    {
        // No prior row; cold-start scan of the 90d window. The window IS the total.
        Assert.Equal(500, ReputationService.ComputeEffectiveTotalJobs(
            windowCount: 500, priorTotal: null, wasColdStartScan: true, step1Trustworthy: true));
    }

    [Fact]
    public void Coldstart_takes_max_of_prior_and_window_does_not_add()
    {
        // Cold-start scan of a known agent. Take the monotonic floor (max), never add
        // — adding the full window onto the prior would double-count the overlap.
        // 21983 in-window jobs, prior 12 => max(12, 21983) = 21983, not 21983+12.
        Assert.Equal(21983, ReputationService.ComputeEffectiveTotalJobs(
            windowCount: 21983, priorTotal: 12, wasColdStartScan: true, step1Trustworthy: true));
    }

    [Fact]
    public void Coldstart_capped_window_below_prior_keeps_prior_no_clobber()
    {
        // The clobber the cold-start cap would cause: an operator zeroes
        // last_scanned_block WITHOUT deleting the row, so priorTotal=12 survives but
        // the cold-start scan is capped to 90d and only sees 3 of the 12 lifetime
        // jobs. max(12, 3) = 12 — the accumulated total must NOT be clobbered to 3.
        Assert.Equal(12, ReputationService.ComputeEffectiveTotalJobs(
            windowCount: 3, priorTotal: 12, wasColdStartScan: true, step1Trustworthy: true));
    }

    [Fact]
    public void Delta_trustworthy_accumulates_onto_prior()
    {
        // The headline fix: a delta window of 40 new jobs ADDS to the prior 21983
        // instead of clobbering it.
        Assert.Equal(22023, ReputationService.ComputeEffectiveTotalJobs(
            windowCount: 40, priorTotal: 21983, wasColdStartScan: false, step1Trustworthy: true));
    }

    [Fact]
    public void Delta_zero_new_jobs_keeps_prior()
    {
        // A quiet day (0 new jobs) must keep the accumulated total, not zero it.
        Assert.Equal(21983, ReputationService.ComputeEffectiveTotalJobs(
            windowCount: 0, priorTotal: 21983, wasColdStartScan: false, step1Trustworthy: true));
    }

    [Fact]
    public void Genuinely_new_agent_persists_honest_zero()
    {
        // A brand-new agent with no on-chain jobs still persists an honest 0
        // (isColdStart=true downstream) — the fold never invents a count.
        Assert.Equal(0, ReputationService.ComputeEffectiveTotalJobs(
            windowCount: 0, priorTotal: null, wasColdStartScan: true, step1Trustworthy: true));
    }

    [Fact]
    public void Untrusted_delta_keeps_prior_does_not_add_partial()
    {
        // step-1 dropped chunks => the window is an undercount. Keep prior and don't
        // add the partial; the scanner also leaves the checkpoint unadvanced so the
        // next clean pass re-scans the same window and accumulates correctly (no
        // double count, because this pass added nothing).
        Assert.Equal(21983, ReputationService.ComputeEffectiveTotalJobs(
            windowCount: 40, priorTotal: 21983, wasColdStartScan: false, step1Trustworthy: false));
    }

    [Fact]
    public void Untrusted_zero_with_prior_keeps_prior()
    {
        // The exact wrong-0 regression we must never persist: an untrustworthy scan
        // that came back ~0 must not overwrite a known-good total.
        Assert.Equal(21983, ReputationService.ComputeEffectiveTotalJobs(
            windowCount: 0, priorTotal: 21983, wasColdStartScan: true, step1Trustworthy: false));
    }

    [Fact]
    public void Untrusted_coldstart_firstever_uses_partial_window_as_best_effort()
    {
        // No prior to fall back to: persist the partial window (best effort). The
        // scanner won't advance the checkpoint, so the next trustworthy cold-start
        // pass replaces it with the full count.
        Assert.Equal(15000, ReputationService.ComputeEffectiveTotalJobs(
            windowCount: 15000, priorTotal: null, wasColdStartScan: true, step1Trustworthy: false));
    }

    [Fact]
    public void Untrusted_delta_no_prior_falls_back_to_window()
    {
        // Inconsistent edge (delta scan but no prior row). Best effort: the window.
        Assert.Equal(40, ReputationService.ComputeEffectiveTotalJobs(
            windowCount: 40, priorTotal: null, wasColdStartScan: false, step1Trustworthy: false));
    }

    [Fact]
    public void Repeated_delta_passes_are_monotonic()
    {
        // Simulate three consecutive daily delta passes after a cold-start baseline
        // of 21983 — the total only ever grows.
        long total = 21983;
        total = ReputationService.ComputeEffectiveTotalJobs(244, total, false, true); // day 1
        Assert.Equal(22227, total);
        total = ReputationService.ComputeEffectiveTotalJobs(0,   total, false, true);  // quiet day
        Assert.Equal(22227, total);
        total = ReputationService.ComputeEffectiveTotalJobs(100, total, false, false); // flaky day -> keep
        Assert.Equal(22227, total);
        total = ReputationService.ComputeEffectiveTotalJobs(100, total, false, true);  // recovers + adds the day
        Assert.Equal(22327, total);
    }
}
