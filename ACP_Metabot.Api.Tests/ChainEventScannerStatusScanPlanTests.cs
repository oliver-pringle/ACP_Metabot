using System.Numerics;
using ACP_Metabot.Api.Services;
using Xunit;

namespace ACP_Metabot.Api.Tests;

// Unit tests for the pure status-scan planner that bounds the O(jobs) per-agent
// status scan. Root cause (2026-06-10): agents with ~20k+ on-chain JobCreated
// (Quiver 21,983; UW Agent 21,952) showed TotalJobs=0 in reputation because the
// per-job status scan never completed. The planner keeps TotalJobs EXACT while
// scanning detailed status for only the most-recent N jobs.
public class ChainEventScannerStatusScanPlanTests
{
    [Fact]
    public void Under_cap_scans_all_jobs_from_full_start_block()
    {
        var jobBlocks = new Dictionary<BigInteger, long> { [1] = 100, [2] = 200, [3] = 300 };
        var plan = ChainEventScanner.PlanStatusScan(jobBlocks, fullScanFromBlock: 50, cap: 1000);

        Assert.False(plan.Capped);
        Assert.Equal(3, plan.TotalJobs);
        Assert.Equal(50, plan.StatusFromBlock);                 // unchanged when not capped
        Assert.Equal(3, plan.StatusJobIds.Count);
    }

    [Fact]
    public void Over_cap_keeps_full_total_but_scans_only_recent_jobs()
    {
        var jobBlocks = new Dictionary<BigInteger, long>();
        for (int i = 1; i <= 10; i++) jobBlocks[i] = i * 100L;  // job i at block i*100; newest = job 10 @1000

        var plan = ChainEventScanner.PlanStatusScan(jobBlocks, fullScanFromBlock: 50, cap: 3);

        Assert.True(plan.Capped);
        Assert.Equal(10, plan.TotalJobs);                       // full count preserved — the headline fix
        Assert.Equal(3, plan.StatusJobIds.Count);               // only the 3 most-recent scanned
        var ids = plan.StatusJobIds.ToHashSet();
        Assert.Contains((BigInteger)10, ids);
        Assert.Contains((BigInteger)9, ids);
        Assert.Contains((BigInteger)8, ids);
        Assert.Equal(800, plan.StatusFromBlock);                // min block among the selected (job 8 @800)
    }

    [Fact]
    public void Cap_zero_or_negative_disables_capping()
    {
        var jobBlocks = new Dictionary<BigInteger, long> { [1] = 10, [2] = 20 };
        var plan = ChainEventScanner.PlanStatusScan(jobBlocks, fullScanFromBlock: 5, cap: 0);

        Assert.False(plan.Capped);
        Assert.Equal(2, plan.StatusJobIds.Count);
        Assert.Equal(5, plan.StatusFromBlock);
    }

    [Fact]
    public void Empty_jobs_returns_empty_plan()
    {
        var plan = ChainEventScanner.PlanStatusScan(new Dictionary<BigInteger, long>(), fullScanFromBlock: 50, cap: 1000);

        Assert.Equal(0, plan.TotalJobs);
        Assert.Empty(plan.StatusJobIds);
        Assert.False(plan.Capped);
    }

    [Fact]
    public void StatusFromBlock_never_below_full_start_block()
    {
        // Even if a selected job's block were below the scan floor, the planner must
        // not scan below fullScanFromBlock (the cold-start window floor).
        var jobBlocks = new Dictionary<BigInteger, long> { [1] = 10, [2] = 20, [3] = 30 };
        var plan = ChainEventScanner.PlanStatusScan(jobBlocks, fullScanFromBlock: 25, cap: 1);
        Assert.True(plan.Capped);
        Assert.Equal(3, plan.TotalJobs);
        Assert.Equal(30, plan.StatusFromBlock);                 // job 3 @30 is the most recent; >= floor 25
    }
}
