using Pasto.Server.Api;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class ApiRateBucketTests
{
    [Theory]
    [InlineData(nameof(ApiOptions.CreatePerIp), 3000, 2)]
    [InlineData(nameof(ApiOptions.CreateGlobal), 750, 1)]
    [InlineData(nameof(ApiOptions.JoinPerIp), 750, 1)]
    [InlineData(nameof(ApiOptions.JoinGlobal), 50, 1)]
    [InlineData(nameof(ApiOptions.ReadPerIp), 3, 1)]
    [InlineData(nameof(ApiOptions.ReadGlobal), 1.5, 1)]
    [InlineData(nameof(ApiOptions.ReadPerParticipant), 375, 1)]
    [InlineData(nameof(ApiOptions.AddPerParticipant), 3000, 2)]
    [InlineData(nameof(ApiOptions.DeletePerParticipant), 1500, 1)]
    public void DefaultBucketRefillsContinuouslyAndRetainsFractionalCredit(string profile, double firstRefillMs, int emptyRetrySeconds)
    {
        var settings = Assert.IsType<BucketOptions>(typeof(ApiOptions).GetProperty(profile)!.GetValue(new ApiOptions()));
        var bucket = new ApiRateBucket(settings);
        var now = TimeSpan.Zero;
        ExhaustBurst();
        Assert.Equal(emptyRetrySeconds, bucket.Check(now));

        // Each timestamp below supplies 1.5 tokens at that bucket's documented sustained rate.
        now = TimeSpan.FromMilliseconds(firstRefillMs);
        Assert.Null(bucket.Check(now));
        bucket.Consume(now);
        Assert.Equal(1, bucket.Check(now));

        // The half-token remains after the first debit. Only one more half-token may unlock the next request.
        var periodTicks = TimeSpan.FromSeconds(settings.PeriodSeconds).Ticks;
        var twoTokens = TimeSpan.FromTicks((2 * periodTicks + settings.TokensPerPeriod - 1) / settings.TokensPerPeriod);
        now = twoTokens - TimeSpan.FromTicks(1);
        Assert.Equal(1, bucket.Check(now));
        now = twoTokens;
        Assert.Null(bucket.Check(now));
        bucket.Consume(now);
        Assert.Equal(emptyRetrySeconds, bucket.Check(now));

        now = TimeSpan.MaxValue;
        Assert.True(bucket.IsFull(now));
        ExhaustBurst();
        Assert.Equal(emptyRetrySeconds, bucket.Check(now));

        void ExhaustBurst()
        {
            for (var request = 0; request < settings.TokenLimit; request++)
            {
                Assert.Null(bucket.Check(now));
                bucket.Consume(now);
            }
        }
    }
}
