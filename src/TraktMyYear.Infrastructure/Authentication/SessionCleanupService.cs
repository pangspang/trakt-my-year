using Microsoft.Extensions.Hosting;
using TraktMyYear.Application;

namespace TraktMyYear.Infrastructure.Authentication;

public sealed class SessionCleanupService(
    IApplicationSessionStore sessionStore,
    IOAuthStateStore oauthStateStore,
    IYearReviewStore yearReviewStore) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            oauthStateStore.CleanupExpired();
            foreach (var accountId in sessionStore.CleanupExpiredSessions())
            {
                await yearReviewStore.RemoveAccountAsync(accountId, stoppingToken);
            }
        }
    }
}
