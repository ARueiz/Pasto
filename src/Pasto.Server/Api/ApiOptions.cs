using System.Net;

namespace Pasto.Server.Api;

public sealed class ApiOptions
{
    public BucketOptions CreatePerIp { get; set; } = new() { TokenLimit = 10, TokensPerPeriod = 30 };
    public BucketOptions CreateGlobal { get; set; } = new() { TokenLimit = 20, TokensPerPeriod = 120 };
    public string[] TrustedProxies { get; set; } = [];

    public bool IsValid() => CreatePerIp is not null && CreateGlobal is not null
        && CreatePerIp.IsValid() && CreateGlobal.IsValid()
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
