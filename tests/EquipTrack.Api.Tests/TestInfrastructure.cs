using System.Collections.Concurrent;
using EquipTrack.Api.Auth;
using EquipTrack.Api.Data;
using EquipTrack.Api.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace EquipTrack.Api.Tests;

/// <summary>Records Brevo calls; can be told to fail the next N calls.</summary>
public sealed class FakeBrevoClient : IBrevoClient
{
    public ConcurrentQueue<BrevoContact> Contacts { get; } = new();
    public ConcurrentQueue<BrevoEmail> Emails { get; } = new();
    public int FailNextCalls { get; set; }

    public Task UpsertContactAsync(BrevoContact contact, CancellationToken ct)
    {
        MaybeFail();
        Contacts.Enqueue(contact);
        return Task.CompletedTask;
    }

    public Task SendEmailAsync(BrevoEmail email, CancellationToken ct)
    {
        MaybeFail();
        Emails.Enqueue(email);
        return Task.CompletedTask;
    }

    private void MaybeFail()
    {
        if (FailNextCalls <= 0) return;
        FailNextCalls--;
        throw new BrevoException("simulated Brevo outage");
    }
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"equiptrack-tests-{Guid.NewGuid()}";

    public const string SysadminEmail = "platform@equiptrack.test";

    /// <summary>Signs test tokens; also serves as the API's JWKS source.</summary>
    public TestTokens Tokens { get; } = new();

    /// <summary>Captures every email the API sends.</summary>
    public FakeBrevoClient Brevo { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", "Host=unused");
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("RateLimiting:LeadsPerMinute", "1000");
        builder.UseSetting("Brevo:ApiKey", "test-key");
        builder.UseSetting("Brevo:SenderEmail", "hello@equiptrack.test");
        builder.UseSetting("Brevo:TeamNotificationEmail", "team@equiptrack.test");
        builder.UseSetting("App:PublicUrl", "https://app.equiptrack.test");
        builder.UseSetting("NeonAuth:BaseUrl", TestTokens.BaseUrl);
        builder.UseSetting("NeonAuth:SysadminEmails:0", SysadminEmail);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<DbContextOptions>();
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(_dbName));

            services.RemoveAll<IBrevoClient>();
            services.AddSingleton<IBrevoClient>(Brevo);

            services.RemoveAll<IJwksProvider>();
            services.AddSingleton<IJwksProvider>(Tokens);

            // The worker is exercised directly through LeadSyncProcessor in tests.
            services.RemoveAll<IHostedService>();
        });
    }

    /// <summary>An HTTP client signed in as the given user.</summary>
    public HttpClient ClientFor(string userId, string email, bool emailVerified = true, string? name = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", Tokens.Issue(userId, email, emailVerified, name));
        return client;
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }
}
