using ACP_Metabot.Api.Services;
using Xunit;

namespace ACP_Metabot.Api.Tests;

public class BlockRangeChunkerTests
{
    [Fact]
    public void EmptyRange_WhenFromGreaterThanTo_ReturnsNoChunks()
    {
        var chunks = BlockRangeChunker.Chunk(100, 50, 10).ToList();
        Assert.Empty(chunks);
    }

    [Fact]
    public void SingleBlockRange_ReturnsOneChunk()
    {
        var chunks = BlockRangeChunker.Chunk(42, 42, 10).ToList();
        Assert.Single(chunks);
        Assert.Equal((42L, 42L), chunks[0]);
    }

    [Fact]
    public void RangeSmallerThanChunk_ReturnsOneChunk()
    {
        var chunks = BlockRangeChunker.Chunk(0, 9, 10).ToList();
        Assert.Single(chunks);
        Assert.Equal((0L, 9L), chunks[0]);
    }

    [Fact]
    public void RangeExactlyOneChunk_ReturnsOneChunk()
    {
        // 10-block window, chunk size 10 → one chunk [0,9].
        var chunks = BlockRangeChunker.Chunk(0, 9, 10).ToList();
        Assert.Single(chunks);
        Assert.Equal((0L, 9L), chunks[0]);
    }

    [Fact]
    public void EvenlyDivisibleRange_ReturnsExactCount()
    {
        // 30-block window, chunk size 10 → 3 chunks: [0,9],[10,19],[20,29].
        var chunks = BlockRangeChunker.Chunk(0, 29, 10).ToList();
        Assert.Equal(3, chunks.Count);
        Assert.Equal((0L, 9L), chunks[0]);
        Assert.Equal((10L, 19L), chunks[1]);
        Assert.Equal((20L, 29L), chunks[2]);
    }

    [Fact]
    public void RangeWithRemainder_LastChunkEndsAtTo()
    {
        // 25-block window, chunk size 10 → 3 chunks: [0,9],[10,19],[20,24].
        var chunks = BlockRangeChunker.Chunk(0, 24, 10).ToList();
        Assert.Equal(3, chunks.Count);
        Assert.Equal((0L, 9L), chunks[0]);
        Assert.Equal((10L, 19L), chunks[1]);
        Assert.Equal((20L, 24L), chunks[2]);
    }

    [Fact]
    public void RangeStartingMidway_RespectsFromBlock()
    {
        // [100, 124], chunk size 10 → [100,109],[110,119],[120,124].
        var chunks = BlockRangeChunker.Chunk(100, 124, 10).ToList();
        Assert.Equal(3, chunks.Count);
        Assert.Equal((100L, 109L), chunks[0]);
        Assert.Equal((110L, 119L), chunks[1]);
        Assert.Equal((120L, 124L), chunks[2]);
    }

    [Fact]
    public void NonPositiveChunkSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BlockRangeChunker.Chunk(0, 100, 0).ToList());
        Assert.Throws<ArgumentOutOfRangeException>(() => BlockRangeChunker.Chunk(0, 100, -1).ToList());
    }

    [Fact]
    public void RealisticBaseScenario_NoOverlapNoGap()
    {
        // Simulate a 6M-block range chunked at 10K — verify total coverage.
        long from = 22_000_000, to = 28_000_000, size = 10_000;
        var chunks = BlockRangeChunker.Chunk(from, to, size).ToList();

        Assert.Equal(from, chunks.First().Start);
        Assert.Equal(to,   chunks.Last().End);

        for (int i = 1; i < chunks.Count; i++)
        {
            Assert.Equal(chunks[i - 1].End + 1, chunks[i].Start); // no gap, no overlap
        }
        foreach (var (s, e) in chunks)
        {
            Assert.True(e - s + 1 <= size);
            Assert.True(e >= s);
        }
    }
}
