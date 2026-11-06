using Lonnii.Api.Features.Auth;

namespace Lonnii.Tests;

public class LoginThrottleTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TimeSpan? FailTimes(LoginThrottle t, int n, string id = "a@b.c", string ip = "1.1.1.1")
    {
        TimeSpan? last = null;
        for (var i = 0; i < n; i++) last = t.Failed(id, ip);
        return last;
    }

    [Fact]
    public void Five_failures_lock_for_five_minutes_then_thirty()
    {
        var clock = new Clock();
        var t = new LoginThrottle(clock);

        Assert.Null(FailTimes(t, 4));
        Assert.Null(t.Remaining("a@b.c", "1.1.1.1"));
        Assert.Equal(LoginThrottle.FirstLock, t.Failed("a@b.c", "1.1.1.1"));
        Assert.NotNull(t.Remaining("A@B.C ", "9.9.9.9")); // same account, any address, any case

        clock.Now += TimeSpan.FromMinutes(6);
        Assert.Null(t.Remaining("a@b.c", "1.1.1.1"));

        Assert.Equal(LoginThrottle.LaterLock, FailTimes(t, 5));
        clock.Now += TimeSpan.FromMinutes(29);
        Assert.NotNull(t.Remaining("a@b.c", "1.1.1.1"));
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Null(t.Remaining("a@b.c", "1.1.1.1"));
    }

    [Fact]
    public void One_address_trying_many_accounts_is_locked()
    {
        var t = new LoginThrottle(new Clock());
        for (var i = 0; i < 5; i++) t.Failed($"user{i}@x.y", "6.6.6.6");

        Assert.NotNull(t.Remaining("someone@else.z", "6.6.6.6"));
        Assert.Null(t.Remaining("someone@else.z", "7.7.7.7"));
    }

    [Fact]
    public void Success_starts_again_from_zero()
    {
        var t = new LoginThrottle(new Clock());
        FailTimes(t, 4);
        t.Succeeded("a@b.c", "1.1.1.1");

        Assert.Null(FailTimes(t, 4));
    }

    [Fact]
    public void Message_says_how_long()
    {
        Assert.Equal("Trop de tentatives incorrectes. Réessayez dans 5 minutes.", LoginThrottle.Message(TimeSpan.FromMinutes(5)));
        Assert.Equal("Trop de tentatives incorrectes. Réessayez dans 1 minute.", LoginThrottle.Message(TimeSpan.FromSeconds(20)));
    }
}
