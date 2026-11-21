using System.Net;

namespace Pasto.Server.Api;

public sealed class ApiOptions
{
    public BucketOptions CreatePerIp { get; set; } = new() { TokenLimit = 10, TokensPerPeriod = 30 };
    public BucketOptions CreateGlobal { get; set; } = new() { TokenLimit = 20, TokensPerPeriod = 120 };
    public string[] TrustedProxies { get; set; } = [];
    public BucketOptions JoinPerIp { get; set; } = new() { TokenLimit = 20, TokensPerPeriod = 120 };
    public BucketOptions JoinGlobal { get; set; } = new() { TokenLimit = 60, TokensPerPeriod = 30, PeriodSeconds = 1 };
    public BucketOptions ReadPerIp { get; set; } = new() { TokenLimit = 1000, TokensPerPeriod = 30000 };
    public BucketOptions ReadGlobal { get; set; } = new() { TokenLimit = 2000, TokensPerPeriod = 60000 };
    public BucketOptions CredentialFailuresPerSource { get; set; } = new() { TokenLimit = 20, TokensPerPeriod = 1, PeriodSeconds = 1 };
    public BucketOptions ReadPerParticipant { get; set; } = new() { TokenLimit = 40, TokensPerPeriod = 240 };
    public BucketOptions AddPerParticipant { get; set; } = new() { TokenLimit = 10, TokensPerPeriod = 30 };
    public BucketOptions DeletePerParticipant { get; set; } = new() { TokenLimit = 10, TokensPerPeriod = 60 };

    public bool IsValid() => new[] { CreatePerIp, CreateGlobal, JoinPerIp, JoinGlobal, ReadPerIp, ReadGlobal, CredentialFailuresPerSource, ReadPerParticipant, AddPerParticipant, DeletePerParticipant }
        .All(bucket => bucket is not null && bucket.IsValid())
        && TrustedProxies is not null && TrustedProxies.All(x => IPAddress.TryParse(x, out _));
}

public sealed class BucketOptions
{
    public int TokenLimit { get; set; }
    public int TokensPerPeriod { get; set; }
    public int PeriodSeconds { get; set; } = 60;
    public bool IsValid() => TokenLimit is >= 1 and <= 100000 && TokensPerPeriod is >= 1 and <= 100000
        && PeriodSeconds is >= 1 and <= 86400;
}

public sealed class DatabaseOptions
{
    public int MaxPoolSize { get; set; } = 64;
}
