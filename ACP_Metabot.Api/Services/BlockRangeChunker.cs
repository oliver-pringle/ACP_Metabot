namespace ACP_Metabot.Api.Services;

public static class BlockRangeChunker
{
    public static IEnumerable<(long Start, long End)> Chunk(long fromBlock, long toBlock, long chunkSize)
    {
        if (chunkSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "chunkSize must be positive");
        if (fromBlock > toBlock) yield break;

        long start = fromBlock;
        while (start <= toBlock)
        {
            long end = Math.Min(start + chunkSize - 1, toBlock);
            yield return (start, end);
            start = end + 1;
        }
    }
}
