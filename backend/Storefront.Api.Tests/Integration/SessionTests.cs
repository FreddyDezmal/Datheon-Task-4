using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Storefront.Api.Data;
using Storefront.Api.Data.Entities;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Integration;

/// <summary>
/// Server-side sessions (Security-Audit.md, SA-02): sign-out, refresh-token rotation and reuse
/// detection, and password changes end a session on the server, for both of its tokens, at once.
/// </summary>
[Trait("Category", TestCategories.Api)]
public sealed class SessionTests : IDisposable
{
    private readonly ManualTimeProvider _clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly StorefrontApiFactory _factory;

    public SessionTests() => _factory = new StorefrontApiFactory(timeProvider: _clock);

    public void Dispose() => _factory.Dispose();

    private const string Password = "Str0ng!Passw0rd";

    private static string Email() => $"session-{Guid.NewGuid():N}@example.com";

    private HttpClient With(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private async Task<HttpResponseMessage> Refresh(string refreshToken) =>
        await _factory.CreateClient().PostAsJsonAsync("/api/account/refresh", new { refreshToken });

    private static async Task<StorefrontApiFactory.TokenResponse> Tokens(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<StorefrontApiFactory.TokenResponse>())!;
    }

    private async Task<string?> RevokedReason(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StorefrontDbContext>();
        return await db.UserSessions.Where(s => s.User!.Email == email).OrderByDescending(s => s.CreatedAtUtc)
            .Select(s => s.RevokedReason).FirstOrDefaultAsync();
    }

    [Fact]
    public async Task Signing_out_ends_the_session_for_both_tokens()
    {
        var email = Email();
        var (_, tokens) = await _factory.SignInAsync(email, Password);
        Assert.Equal(HttpStatusCode.OK, (await With(tokens.AccessToken).GetAsync("/api/orders")).StatusCode);

        var logout = await _factory.CreateClient().PostAsJsonAsync("/api/account/logout", new { refreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await With(tokens.AccessToken).GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await With(tokens.AccessToken).GetAsync("/api/account/manage/info")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(tokens.RefreshToken)).StatusCode);
        Assert.Equal("signed-out", await RevokedReason(email));
    }

    [Fact]
    public async Task A_revoked_token_cannot_place_an_order_in_the_account_or_as_a_guest()
    {
        var (_, tokens) = await _factory.SignInAsync(Email(), Password);
        await _factory.CreateClient().PostAsJsonAsync("/api/account/logout", new { refreshToken = tokens.RefreshToken });

        var checkout = await With(tokens.AccessToken).PostAsJsonAsync("/api/checkout", AuthorizationTests.Order((2, 1)));

        Assert.Equal(HttpStatusCode.Unauthorized, checkout.StatusCode);
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<StorefrontDbContext>().Orders.CountAsync());
    }

    [Fact]
    public async Task Signing_out_with_only_the_access_token_also_works()
    {
        var (client, tokens) = await _factory.SignInAsync(Email(), Password);

        var logout = await client.PostAsync("/api/account/logout", JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(tokens.RefreshToken)).StatusCode);
    }

    [Theory]
    [InlineData("{\"refreshToken\":\"garbage\"}")]
    [InlineData("{}")]
    [InlineData("{\"refreshToken\":\"\"}")]
    public async Task Logout_always_answers_204_and_reveals_nothing(string body)
    {
        var response = await _factory.CreateClient().PostAsync("/api/account/logout", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Each_refresh_token_works_once_and_reusing_one_ends_the_session()
    {
        var email = Email();
        var (_, first) = await _factory.SignInAsync(email, Password);
        var second = await Tokens(await Refresh(first.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, (await With(second.AccessToken).GetAsync("/api/orders")).StatusCode);

        _clock.Advance(TimeSpan.FromSeconds(31)); // past the grace period
        var replay = await Refresh(first.RefreshToken); // someone replays the old (stolen) token

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        // The whole session is gone: the legitimate holder's newer tokens stop too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(second.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await With(second.AccessToken).GetAsync("/api/orders")).StatusCode);
        Assert.Equal("refresh-token-reused", await RevokedReason(email));
    }

    [Fact]
    public async Task A_retry_within_the_grace_period_after_a_lost_response_keeps_the_session()
    {
        var (_, first) = await _factory.SignInAsync(Email(), Password);
        await Tokens(await Refresh(first.RefreshToken)); // the response that "got lost"

        _clock.Advance(TimeSpan.FromSeconds(10));
        var retried = await Tokens(await Refresh(first.RefreshToken));

        Assert.Equal(HttpStatusCode.OK, (await With(retried.AccessToken).GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Refresh(retried.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task Parallel_refreshes_of_the_same_token_do_not_end_the_session()
    {
        var (_, tokens) = await _factory.SignInAsync(Email(), Password);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Refresh(tokens.RefreshToken)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var issued = await Tokens(responses[^1]);
        Assert.Equal(HttpStatusCode.OK, (await With(issued.AccessToken).GetAsync("/api/orders")).StatusCode);
    }

    [Fact]
    public async Task A_password_change_ends_existing_sessions_immediately()
    {
        var email = Email();
        var (_, tokens) = await _factory.SignInAsync(email, Password);

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StorefrontUser>>();
            var user = await users.FindByEmailAsync(email);
            Assert.True((await users.ChangePasswordAsync(user!, Password, "N3w!Passw0rd-x")).Succeeded);
        }

        // Not after the access token's hour: at once.
        Assert.Equal(HttpStatusCode.Unauthorized, (await With(tokens.AccessToken).GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(tokens.RefreshToken)).StatusCode);
        var again = await _factory.CreateClient().PostAsJsonAsync("/api/account/login", new { email, password = "N3w!Passw0rd-x" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Tokens_issued_before_server_side_sessions_are_refused()
    {
        var email = Email();
        await _factory.SignInAsync(email, Password);
        string access, refresh;
        using (var scope = _factory.Services.CreateScope())
        {
            // A token exactly like the ones MapIdentityApi used to issue: the user's claims, no session.
            var users = scope.ServiceProvider.GetRequiredService<UserManager<StorefrontUser>>();
            var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<StorefrontUser>>();
            var principal = await factory.CreateAsync((await users.FindByEmailAsync(email))!);
            var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<BearerTokenOptions>>().Get(IdentityConstants.BearerScheme);
            access = options.BearerTokenProtector.Protect(new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = _clock.GetUtcNow().AddHours(1) }, "Identity.Bearer"));
            refresh = options.RefreshTokenProtector.Protect(new AuthenticationTicket(principal, new AuthenticationProperties { ExpiresUtc = _clock.GetUtcNow().AddDays(14) }, "Identity.Bearer:RefreshToken"));
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await With(access).GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(refresh)).StatusCode);
    }

    [Fact]
    public async Task Refreshing_never_extends_a_session_past_14_days()
    {
        var (_, tokens) = await _factory.SignInAsync(Email(), Password);
        for (var day = 0; day < 13; day++)
        {
            _clock.Advance(TimeSpan.FromDays(1));
            tokens = await Tokens(await Refresh(tokens.RefreshToken));
        }

        _clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));

        Assert.Equal(HttpStatusCode.Unauthorized, (await Refresh(tokens.RefreshToken)).StatusCode);
    }

    [Fact]
    public async Task An_account_keeps_at_most_ten_active_sessions_and_the_oldest_ends_first()
    {
        var email = Email();
        var (_, oldest) = await _factory.SignInAsync(email, Password);
        for (var i = 0; i < 10; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await _factory.CreateClient().PostAsJsonAsync("/api/account/login", new { email, password = Password });
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await With(oldest.AccessToken).GetAsync("/api/orders")).StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StorefrontDbContext>();
        Assert.Equal(10, await db.UserSessions.CountAsync(s => s.User!.Email == email && s.RevokedAtUtc == null));
    }

    [Fact]
    public async Task Revocation_is_stored_in_the_database_so_a_new_instance_honours_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"storefront-sessions-{Guid.NewGuid():N}.db");
        try
        {
            StorefrontApiFactory.TokenResponse tokens;
            using (var first = new StorefrontApiFactory(databasePath: path))
            {
                (_, tokens) = await first.SignInAsync(Email(), Password);
                await first.CreateClient().PostAsJsonAsync("/api/account/logout", new { refreshToken = tokens.RefreshToken });
            }

            using var second = new StorefrontApiFactory(databasePath: path);
            var client = second.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/orders")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await second.CreateClient().PostAsJsonAsync("/api/account/refresh", new { refreshToken = tokens.RefreshToken })).StatusCode);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            TestDatabase.TryDelete(path);
        }
    }

    [Fact]
    public async Task Ending_one_shoppers_session_leaves_other_shoppers_signed_in()
    {
        var (aliceClient, _) = await _factory.SignInAsync(Email(), Password);
        var (bobClient, bobTokens) = await _factory.SignInAsync(Email(), Password);
        await _factory.CreateClient().PostAsJsonAsync("/api/account/logout", new { refreshToken = bobTokens.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, (await aliceClient.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bobClient.GetAsync("/api/orders")).StatusCode);
    }
}
