using System.Net;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Routing;
using Plutus.Core.Data;
using Plutus.Web.Authentication;
using Plutus.Web.Components;

namespace Plutus.Web.Tests;

public sealed class AuthenticationTests
{
    [Fact]
    public void Missing_or_malformed_password_hash_fails_closed_without_echoing_the_value()
    {
        var missing = Assert.Throws<InvalidOperationException>(() =>
            PlutusAuthentication.GetRequiredPasswordHash(() => null));
        var malformedValue = "not-a-password-hash";
        var malformed = Assert.Throws<InvalidOperationException>(() =>
            PlutusAuthentication.GetRequiredPasswordHash(() => malformedValue));

        Assert.Contains(PlutusAuthentication.PasswordHashEnvironmentVariable, missing.Message);
        Assert.DoesNotContain(malformedValue, malformed.Message);
    }

    [Fact]
    public void Password_hash_generator_reads_interactively_and_never_echoes_the_password()
    {
        var password = $"test-{Guid.NewGuid():N}";
        using var input = new StringReader($"{password}\n{password}\n");
        using var output = new StringWriter();

        PasswordHashGenerator.Run(input, output);

        var generated = output.ToString();
        var hash = generated.Split('\n')
            .Single(line => line.StartsWith($"{PlutusAuthentication.PasswordHashEnvironmentVariable}=", StringComparison.Ordinal))
            .Split('=', 2)[1]
            .Trim();

        Assert.True(PlutusAuthentication.VerifyPassword(hash, password));
        Assert.DoesNotContain(password, generated);
        Assert.True(PasswordHashGenerator.IsRequested([PasswordHashGenerator.Command]));
        Assert.Throws<InvalidOperationException>(() =>
            PasswordHashGenerator.Run(new StringReader($"{new string('x', PasswordHashGenerator.MinimumPasswordLength - 1)}\n"), new StringWriter()));
    }

    [Fact]
    public async Task Finance_routes_redirect_anonymous_users_to_login()
    {
        await using var app = await TestApplication.StartAsync();

        var response = await app.Client.GetAsync("/finance");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location?.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_rejects_requests_without_an_antiforgery_token()
    {
        await using var app = await TestApplication.StartAsync();

        var response = await app.Client.PostAsync("/login", new FormUrlEncodedContent(
        [new KeyValuePair<string, string>("password", app.Password)]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_rejects_a_bad_password_without_logging_password_or_hash()
    {
        await using var app = await TestApplication.StartAsync();
        var login = await app.GetLoginAsync();
        var incorrectPassword = $"wrong-{Guid.NewGuid():N}";

        var response = await app.PostLoginAsync(login, incorrectPassword, "/finance");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(incorrectPassword, app.Logs, StringComparison.Ordinal);
        Assert.DoesNotContain(app.PasswordHash, app.Logs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Login_creates_a_secure_session_and_uses_only_safe_return_urls()
    {
        await using var app = await TestApplication.StartAsync();
        var login = await app.GetLoginAsync();

        var rejectedRedirect = await app.PostLoginAsync(login, app.Password, "https://example.test/");

        Assert.Equal(HttpStatusCode.Redirect, rejectedRedirect.StatusCode);
        Assert.Equal("/", rejectedRedirect.Headers.Location?.OriginalString);
        var sessionCookie = TestApplication.GetCookie(rejectedRedirect, app.SessionCookieName);
        AssertSecureHostCookie(sessionCookie);
        AssertSecureHostCookie(login.Cookie);

        var authenticated = await app.GetAsync("/finance", sessionCookie);
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [Fact]
    public async Task Logout_requires_post_and_antiforgery_then_invalidates_the_session()
    {
        await using var app = await TestApplication.StartAsync();

        var getLogout = await app.Client.GetAsync("/logout");
        var anonymousLogout = await app.Client.PostAsync("/logout", new FormUrlEncodedContent([]));
        // GET is never allowed to mutate authentication state; with the global
        // authorization boundary it is challenged rather than reaching sign-out.
        Assert.Equal(HttpStatusCode.Redirect, getLogout.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, anonymousLogout.StatusCode);

        var login = await app.GetLoginAsync();
        var signIn = await app.PostLoginAsync(login, app.Password, "/finance");
        var sessionCookie = TestApplication.GetCookie(signIn, app.SessionCookieName);
        var logoutToken = await app.GetLoginAsync(sessionCookie);

        var logout = await app.PostAsync(
            "/logout",
            new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("__RequestVerificationToken", logoutToken.Token)]),
            $"{sessionCookie}; {logoutToken.Cookie}");

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/login", logout.Headers.Location?.OriginalString);
        var clearedSession = TestApplication.GetCookie(logout, app.SessionCookieName);
        Assert.Contains("expires=", clearedSession, StringComparison.OrdinalIgnoreCase);

        // The durable session record also rejects a retained cookie from another
        // tab, rather than merely relying on this browser honoring Set-Cookie.
        var afterLogout = await app.GetAsync("/finance", sessionCookie);
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Durable_sessions_reject_expiry_and_password_hash_rotation()
    {
        await using var app = await TestApplication.StartAsync();
        var expired = await app.CreateSessionAsync(
            PlutusAuthentication.GetPasswordHashFingerprint(app.PasswordHash),
            DateTime.UtcNow.AddSeconds(-1));

        Assert.False(await app.IsSessionValidAsync(expired.Id, expired.PasswordHashFingerprint));
        Assert.False(await app.IsSessionValidAsync(
            expired.Id,
            PlutusAuthentication.GetPasswordHashFingerprint($"rotated-{Guid.NewGuid():N}")));
        var ticket = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(PlutusAuthentication.PasswordHashFingerprintClaimType, expired.PasswordHashFingerprint)],
            "test"));
        Assert.False(PlutusAuthentication.HasExpectedFingerprint(
            ticket,
            PlutusAuthentication.GetPasswordHashFingerprint($"rotated-{Guid.NewGuid():N}")));
        Assert.IsType<AdministratorRevalidatingAuthenticationStateProvider>(await app.GetAuthenticationStateProviderAsync());
    }

    [Fact]
    public async Task Password_hash_rotation_is_durable_across_a_rollback_to_a_previous_hash()
    {
        var oldPassword = $"old-{Guid.NewGuid():N}";
        var oldHash = new PasswordHasher<object>().HashPassword(new object(), oldPassword);
        await using var original = await TestApplication.StartAsync(password: oldPassword, passwordHash: oldHash);
        var login = await original.GetLoginAsync();
        var signIn = await original.PostLoginAsync(login, oldPassword, "/finance");
        var oldCookie = TestApplication.GetCookie(signIn, original.SessionCookieName);

        var rotatedPassword = $"new-{Guid.NewGuid():N}";
        var rotatedHash = new PasswordHasher<object>().HashPassword(new object(), rotatedPassword);
        await using var rotated = await TestApplication.StartAsync(
            password: rotatedPassword,
            passwordHash: rotatedHash,
            databasePath: original.DatabasePath,
            dataProtectionPath: original.DataProtectionPath);

        Assert.Equal(HttpStatusCode.Redirect, (await rotated.GetAsync("/finance", oldCookie)).StatusCode);

        await using var rolledBack = await TestApplication.StartAsync(
            password: oldPassword,
            passwordHash: oldHash,
            databasePath: original.DatabasePath,
            dataProtectionPath: original.DataProtectionPath);

        // The H1 ticket is cryptographically valid again after configuration
        // rollback, but its server session was permanently revoked during H2
        // startup and must not be resurrected.
        Assert.Equal(HttpStatusCode.Redirect, (await rolledBack.GetAsync("/finance", oldCookie)).StatusCode);
    }

    [Fact]
    public async Task A_retained_cookie_is_rejected_at_the_http_boundary_when_its_server_session_expires()
    {
        await using var app = await TestApplication.StartAsync();
        var login = await app.GetLoginAsync();
        var signIn = await app.PostLoginAsync(login, app.Password, "/finance");
        var sessionCookie = TestApplication.GetCookie(signIn, app.SessionCookieName);

        await app.ExpireSessionsAsync();

        Assert.Equal(HttpStatusCode.Redirect, (await app.GetAsync("/finance", sessionCookie)).StatusCode);
    }

    [Fact]
    public async Task Development_uses_the_same_secure_host_cookie_policy_as_production()
    {
        await using var app = await TestApplication.StartAsync(environmentName: "Development");
        var login = await app.GetLoginAsync();
        var signIn = await app.PostLoginAsync(login, app.Password, "/finance");
        var sessionCookie = TestApplication.GetCookie(signIn, app.SessionCookieName);

        Assert.Equal("__Host-plutus", app.SessionCookieName);
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await app.GetAsync("/finance", sessionCookie)).StatusCode);
    }

    [Fact]
    public async Task Session_store_failure_fails_closed_and_transport_closes_connections_on_ticket_expiration()
    {
        await using var app = await TestApplication.StartAsync();
        var login = await app.GetLoginAsync();
        var signIn = await app.PostLoginAsync(login, app.Password, "/finance");
        var sessionCookie = TestApplication.GetCookie(signIn, app.SessionCookieName);

        Assert.NotEmpty(app.ConnectionEndpointOptions);
        Assert.All(app.ConnectionEndpointOptions, options =>
            Assert.True(options.CloseOnAuthenticationExpiration));
        await app.BreakSessionStoreAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await app.GetAsync("/finance", sessionCookie)).StatusCode);
    }

    [Fact]
    public async Task Logout_cancels_and_drains_active_operation_leases_before_persisting_revocation()
    {
        await using var app = await TestApplication.StartAsync();
        var session = await app.CreateSessionAsync(
            PlutusAuthentication.GetPasswordHashFingerprint(app.PasswordHash),
            DateTime.UtcNow.AddMinutes(5));
        using var lease = await app.AcquireLeaseAsync(session.Id, session.PasswordHashFingerprint);
        Assert.NotNull(lease);

        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = lease!.CancellationToken.Register(() => cancelled.TrySetResult());
        var revoke = app.RevokeAndDrainAsync(session.Id);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(revoke.IsCompleted);
        Assert.Null(await app.AcquireLeaseAsync(session.Id, session.PasswordHashFingerprint));

        lease.Dispose();
        await revoke;
        Assert.False(await app.IsSessionValidAsync(session.Id, session.PasswordHashFingerprint));
    }

    [Fact]
    public async Task Logout_cancellation_callbacks_can_reenter_without_deadlocking_or_leasing_after_revocation()
    {
        await using var app = await TestApplication.StartAsync();
        var session = await app.CreateSessionAsync(
            PlutusAuthentication.GetPasswordHashFingerprint(app.PasswordHash),
            DateTime.UtcNow.AddMinutes(5));
        using var lease = await app.AcquireLeaseAsync(session.Id, session.PasswordHashFingerprint);
        Assert.NotNull(lease);

        var reentrantAcquireWasDenied = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var throwingRegistration = lease!.CancellationToken.Register(static () =>
            throw new InvalidOperationException("A cancellation callback failed."));
        using var reentrantRegistration = lease.CancellationToken.Register(() =>
        {
            var reentrantLease = app.AcquireLeaseAsync(session.Id, session.PasswordHashFingerprint)
                .GetAwaiter().GetResult();
            reentrantAcquireWasDenied.TrySetResult(reentrantLease is null);
            reentrantLease?.Dispose();
            lease.Dispose();
        });

        await app.RevokeAndDrainAsync(session.Id).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(await reentrantAcquireWasDenied.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(await app.IsSessionValidAsync(session.Id, session.PasswordHashFingerprint));
    }

    [Fact]
    public async Task Logout_cancels_an_acquisition_while_its_durable_validation_is_in_flight()
    {
        await using var app = await TestApplication.StartAsync();
        var session = await app.CreateSessionAsync(
            PlutusAuthentication.GetPasswordHashFingerprint(app.PasswordHash),
            DateTime.UtcNow.AddMinutes(5));
        var validationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validationCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var acquisition = app.AcquireBlockedLeaseAsync(session.Id, validationStarted, validationCancelled);
        await validationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var revocation = app.RevokeAndDrainAsync(session.Id);
        await validationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(await acquisition.WaitAsync(TimeSpan.FromSeconds(2)));
        await revocation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(await app.IsSessionValidAsync(session.Id, session.PasswordHashFingerprint));
    }

    [Fact]
    public async Task Session_migration_preserves_existing_data_from_the_pre_authentication_schema()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"plutus-migration-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<PlutusDbContext>()
                .UseSqlite($"Data Source={databasePath}")
                .Options;
            await using var db = new PlutusDbContext(options);
            await db.Database.MigrateAsync("20260608183204_AddTransferCategoryAndExcludeFromSpending");
            db.Categories.Add(new Plutus.Core.Models.Category
            {
                Name = "Existing data",
                Color = "#123456",
                IsSystem = false,
                SortOrder = 99,
            });
            await db.SaveChangesAsync();

            await db.Database.MigrateAsync();

            var preservedCategory = await db.Categories.SingleAsync(category => category.SortOrder == 99);
            Assert.Equal("Existing data", preservedCategory.Name);
            Assert.False(await db.AdministratorSessions.AnyAsync());
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-wal");
            File.Delete($"{databasePath}-shm");
        }
    }

    private static void AssertSecureHostCookie(string cookie)
    {
        Assert.StartsWith("__Host-", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestApplication : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly CapturingLoggerProvider _loggerProvider;

        private readonly string _databasePath;
        private readonly string _dataProtectionPath;
        private readonly bool _ownsDatabasePath;
        private readonly bool _ownsDataProtectionPath;

        private TestApplication(
            WebApplication application,
            CapturingLoggerProvider loggerProvider,
            string password,
            string passwordHash,
            string databasePath,
            string dataProtectionPath,
            bool ownsDatabasePath,
            bool ownsDataProtectionPath,
            string scheme)
        {
            _application = application;
            _loggerProvider = loggerProvider;
            Password = password;
            PasswordHash = passwordHash;
            _databasePath = databasePath;
            _dataProtectionPath = dataProtectionPath;
            _ownsDatabasePath = ownsDatabasePath;
            _ownsDataProtectionPath = ownsDataProtectionPath;
            Client = application.GetTestClient();
            Client.BaseAddress = new Uri($"{scheme}://localhost");
        }

        public HttpClient Client { get; }
        public string Password { get; }
        public string PasswordHash { get; }
        public string DatabasePath => _databasePath;
        public string DataProtectionPath => _dataProtectionPath;
        public string SessionCookieName => "__Host-plutus";
        public string Logs => _loggerProvider.Messages;

        public static async Task<TestApplication> StartAsync(
            string environmentName = "Testing",
            string scheme = "https",
            string? password = null,
            string? passwordHash = null,
            string? databasePath = null,
            string? dataProtectionPath = null)
        {
            password ??= $"test-{Guid.NewGuid():N}";
            passwordHash ??= new PasswordHasher<object>().HashPassword(new object(), password);
            var loggerProvider = new CapturingLoggerProvider();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = environmentName,
            });
            builder.WebHost.UseTestServer();
            var ownsDatabasePath = databasePath is null;
            databasePath ??= Path.Combine(Path.GetTempPath(), $"plutus-auth-{Guid.NewGuid():N}.db");
            var ownsDataProtectionPath = dataProtectionPath is null;
            dataProtectionPath ??= Path.Combine(Path.GetTempPath(), $"plutus-auth-keys-{Guid.NewGuid():N}");
            builder.Services.AddSingleton<ILoggerProvider>(loggerProvider);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddDbContextFactory<PlutusDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
            builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
            builder.Services.AddSingleAdministratorAuthentication(passwordHash);
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();

            var application = builder.Build();
            application.UseRateLimiter();
            application.UseAuthentication();
            application.UseAuthorization();
            application.UseAntiforgery();
            application.MapSingleAdministratorAuthentication();
            application.MapGet("/finance", () => Results.Ok("protected finance data"));
            application.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode()
                .CloseConnectionsOnAuthenticationExpiration()
                .RequireAuthorization();
            await using (var scope = application.Services.CreateAsyncScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlutusDbContext>>();
                await using var db = await factory.CreateDbContextAsync();
                await db.Database.MigrateAsync();
                var sessions = scope.ServiceProvider.GetRequiredService<AdministratorSessionStore>();
                await sessions.RevokeActiveSessionsWithDifferentFingerprintAsync(
                    PlutusAuthentication.GetPasswordHashFingerprint(passwordHash));
            }
            await application.StartAsync();

            return new TestApplication(
                application,
                loggerProvider,
                password,
                passwordHash,
                databasePath,
                dataProtectionPath,
                ownsDatabasePath,
                ownsDataProtectionPath,
                scheme);
        }

        public async Task<LoginToken> GetLoginAsync(string? sessionCookie = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/login");
            if (sessionCookie is not null)
            {
                request.Headers.Add("Cookie", sessionCookie);
            }

            using var response = await Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            var token = Regex.Match(body, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
            Assert.False(string.IsNullOrEmpty(token));
            return new LoginToken(token, GetCookie(response, "__Host-plutus-antiforgery"));
        }

        public Task<HttpResponseMessage> PostLoginAsync(LoginToken login, string password, string returnUrl) =>
            PostAsync(
                "/login",
                new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("__RequestVerificationToken", login.Token),
                    new KeyValuePair<string, string>("password", password),
                    new KeyValuePair<string, string>("returnUrl", returnUrl),
                ]),
                login.Cookie);

        public async Task<HttpResponseMessage> GetAsync(string path, string cookie)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Cookie", cookie);
            return await Client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> PostAsync(string path, HttpContent content, string cookie)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
            request.Headers.Add("Cookie", cookie);
            return await Client.SendAsync(request);
        }

        public static string GetCookie(HttpResponseMessage response, string name)
        {
            var cookie = response.Headers.GetValues("Set-Cookie")
                .SingleOrDefault(value => value.StartsWith(name, StringComparison.Ordinal));
            Assert.NotNull(cookie);
            return cookie!;
        }

        public async Task<Plutus.Core.Models.AdministratorSession> CreateSessionAsync(string fingerprint, DateTime expiresAt)
        {
            await using var scope = _application.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AdministratorSessionStore>().CreateAsync(fingerprint, expiresAt);
        }

        public async Task<bool> IsSessionValidAsync(Guid sessionId, string fingerprint)
        {
            await using var scope = _application.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AdministratorSessionStore>().IsValidAsync(sessionId, fingerprint);
        }

        public async Task<AuthenticationStateProvider> GetAuthenticationStateProviderAsync()
        {
            await using var scope = _application.Services.CreateAsyncScope();
            return scope.ServiceProvider.GetRequiredService<AuthenticationStateProvider>();
        }

        public async Task ExpireSessionsAsync()
        {
            await using var scope = _application.Services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlutusDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await db.AdministratorSessions.ExecuteUpdateAsync(setters =>
                setters.SetProperty(session => session.ExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        }

        public IReadOnlyList<HttpConnectionDispatcherOptions> ConnectionEndpointOptions => _application.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .SelectMany(endpoint => endpoint.Metadata.OfType<HttpConnectionDispatcherOptions>())
            .ToList();

        public async Task BreakSessionStoreAsync()
        {
            await using var scope = _application.Services.CreateAsyncScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<PlutusDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE AdministratorSessions");
        }

        public async Task<AdministratorSessionOperationCoordinator.AdministratorSessionOperationLease?> AcquireLeaseAsync(
            Guid sessionId,
            string fingerprint)
        {
            await using var scope = _application.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<AdministratorSessionStore>();
            var coordinator = scope.ServiceProvider.GetRequiredService<AdministratorSessionOperationCoordinator>();
            return await coordinator.TryAcquireAsync(sessionId, fingerprint, store);
        }

        public async Task<AdministratorSessionOperationCoordinator.AdministratorSessionOperationLease?> AcquireBlockedLeaseAsync(
            Guid sessionId,
            TaskCompletionSource validationStarted,
            TaskCompletionSource validationCancelled)
        {
            await using var scope = _application.Services.CreateAsyncScope();
            var coordinator = scope.ServiceProvider.GetRequiredService<AdministratorSessionOperationCoordinator>();
            return await coordinator.TryAcquireAsync(sessionId, async cancellationToken =>
            {
                validationStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    validationCancelled.TrySetResult();
                    throw;
                }
            });
        }

        public async Task RevokeAndDrainAsync(Guid sessionId)
        {
            await using var scope = _application.Services.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<AdministratorSessionStore>();
            var coordinator = scope.ServiceProvider.GetRequiredService<AdministratorSessionOperationCoordinator>();
            await coordinator.RevokeAndDrainAsync(sessionId, cancellationToken => store.RevokeAsync(sessionId, cancellationToken));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync();
            if (_ownsDatabasePath)
            {
                File.Delete(_databasePath);
            }

            if (_ownsDataProtectionPath)
            {
                Directory.Delete(_dataProtectionPath, recursive: true);
            }
        }
    }

    private sealed record LoginToken(string Token, string Cookie);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];
        public string Messages => string.Join("\n", _messages);
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);
        public void Dispose() { }
    }

    private sealed class CapturingLogger(List<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Add(formatter(state, exception));
    }
}
