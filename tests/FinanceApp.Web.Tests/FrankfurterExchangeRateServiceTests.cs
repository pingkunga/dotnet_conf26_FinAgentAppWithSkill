using System.Net;
using System.Text;
using FinanceApp.Web.Services.ExchangeRates;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinanceApp.Web.Tests;

/// <summary>
/// <see cref="FrankfurterExchangeRateService"/> against a fake <see cref="HttpMessageHandler"/> — no real
/// network call (this project's testing bar, same as its python3/vision-model/live-LLM gaps: verified only
/// against a faked transport here, not the actual api.frankfurter.dev endpoint).
/// </summary>
public sealed class FrankfurterExchangeRateServiceTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static FrankfurterExchangeRateService CreateService(FakeHandler handler, IMemoryCache? cache = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.frankfurter.dev/") };
        return new FrankfurterExchangeRateService(httpClient, cache ?? new MemoryCache(new MemoryCacheOptions()),
            NullLogger<FrankfurterExchangeRateService>.Instance);
    }

    [Fact]
    public async Task GetRateAsync_SameCurrency_ShortCircuitsWithoutCallingTheApi()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("should not be called"));
        var service = CreateService(handler);

        var rate = await service.GetRateAsync("USD", "USD");

        Assert.Equal(1m, rate);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task GetRateAsync_SuccessfulResponse_ParsesTheRequestedRate()
    {
        var handler = new FakeHandler(_ => JsonResponse("""{"amount":1,"base":"USD","date":"2026-09-22","rates":{"THB":35.62}}"""));
        var service = CreateService(handler);

        var rate = await service.GetRateAsync("USD", "THB");

        Assert.Equal(35.62m, rate);
    }

    [Fact]
    public async Task GetRateAsync_SecondCallSameDay_UsesTheCacheInsteadOfCallingTheApiAgain()
    {
        var handler = new FakeHandler(_ => JsonResponse("""{"amount":1,"base":"USD","date":"2026-09-22","rates":{"THB":35.62}}"""));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(handler, cache);

        await service.GetRateAsync("USD", "THB");
        await service.GetRateAsync("USD", "THB");

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task GetRateAsync_ApiUnreachable_FallsBackToTheLastKnownGoodRate()
    {
        var succeeding = true;
        var handler = new FakeHandler(_ => succeeding
            ? JsonResponse("""{"amount":1,"base":"USD","date":"2026-09-22","rates":{"THB":35.62}}""")
            : throw new HttpRequestException("simulated network failure"));
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = CreateService(handler, cache);

        var firstRate = await service.GetRateAsync("USD", "THB");
        Assert.Equal(35.62m, firstRate);

        // Force a cache miss for "today" so the next call actually hits the (now-failing) transport.
        cache.Remove("fx:USD:THB:" + DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd"));
        succeeding = false;

        var secondRate = await service.GetRateAsync("USD", "THB");

        Assert.Equal(35.62m, secondRate);
    }

    [Fact]
    public async Task GetRateAsync_ApiUnreachableWithNothingCached_ReturnsNull()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("simulated network failure"));
        var service = CreateService(handler);

        var rate = await service.GetRateAsync("USD", "THB");

        Assert.Null(rate);
    }
}
