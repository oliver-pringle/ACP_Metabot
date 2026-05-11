using ACP_Metabot.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ACP_Metabot.Api.Tests;

public class ChainedEmbeddingProviderTests
{
    [Fact]
    public async Task PrimarySucceeds_ReturnsPrimaryResult_NoFallback()
    {
        var primary = new FakeProvider("primary", 1024, throwOnce: false);
        var fallback = new FakeProvider("fallback", 1024, throwOnce: false);
        var chain = new ChainedEmbeddingProvider(
            new IEmbeddingProvider[] { primary, fallback },
            NullLogger<ChainedEmbeddingProvider>.Instance);

        var result = await chain.EmbedAsync(new[] { "hello" }, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(0, fallback.CallCount);
        Assert.Equal("primary", chain.ModelId);
        Assert.Equal(1024, chain.Dimension);
    }

    [Fact]
    public async Task PrimaryFails_FallsBackToSecondary()
    {
        var primary = new FakeProvider("primary", 1024, throwOnce: true);
        var fallback = new FakeProvider("fallback", 1024, throwOnce: false);
        var chain = new ChainedEmbeddingProvider(
            new IEmbeddingProvider[] { primary, fallback },
            NullLogger<ChainedEmbeddingProvider>.Instance);

        var result = await chain.EmbedAsync(new[] { "hi", "world" }, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(1, primary.CallCount);
        Assert.Equal(1, fallback.CallCount);
    }

    [Fact]
    public async Task AllProvidersFail_ThrowsLastException()
    {
        var primary = new FakeProvider("primary", 1024, throwAlways: true);
        var fallback = new FakeProvider("fallback", 1024, throwAlways: true);
        var chain = new ChainedEmbeddingProvider(
            new IEmbeddingProvider[] { primary, fallback },
            NullLogger<ChainedEmbeddingProvider>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => chain.EmbedAsync(new[] { "hi" }, CancellationToken.None));
    }

    [Fact]
    public void DimensionMismatch_ThrowsAtConstruction()
    {
        var p1024 = new FakeProvider("p1", 1024, throwOnce: false);
        var p512 = new FakeProvider("p2", 512, throwOnce: false);

        Assert.Throws<InvalidOperationException>(() =>
            new ChainedEmbeddingProvider(
                new IEmbeddingProvider[] { p1024, p512 },
                NullLogger<ChainedEmbeddingProvider>.Instance));
    }

    [Fact]
    public async Task Cancellation_PropagatesWithoutFallback()
    {
        var primary = new FakeProvider("primary", 1024, throwAlways: false);
        var fallback = new FakeProvider("fallback", 1024, throwAlways: false);
        var chain = new ChainedEmbeddingProvider(
            new IEmbeddingProvider[] { primary, fallback },
            NullLogger<ChainedEmbeddingProvider>.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => chain.EmbedAsync(new[] { "hi" }, cts.Token));
        Assert.Equal(0, fallback.CallCount); // never reached
    }

    [Fact]
    public void EmptyChain_ThrowsAtConstruction()
    {
        Assert.Throws<ArgumentException>(() =>
            new ChainedEmbeddingProvider(
                Array.Empty<IEmbeddingProvider>(),
                NullLogger<ChainedEmbeddingProvider>.Instance));
    }

    private sealed class FakeProvider : IEmbeddingProvider
    {
        public string ModelId { get; }
        public int Dimension { get; }
        public int CallCount { get; private set; }

        private readonly bool _throwAlways;
        private bool _throwOnce;

        public FakeProvider(string id, int dim, bool throwOnce = false, bool throwAlways = false)
        {
            ModelId = id;
            Dimension = dim;
            _throwOnce = throwOnce;
            _throwAlways = throwAlways;
        }

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> texts, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            if (_throwAlways) throw new InvalidOperationException($"{ModelId} always fails");
            if (_throwOnce)
            {
                _throwOnce = false;
                throw new InvalidOperationException($"{ModelId} first call fails");
            }
            var result = texts.Select(_ => new float[Dimension]).ToArray();
            return Task.FromResult<IReadOnlyList<float[]>>(result);
        }
    }
}
