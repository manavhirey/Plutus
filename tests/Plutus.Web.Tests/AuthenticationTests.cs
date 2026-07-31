using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plutus.Web.Authentication;

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
        var sessionCookie = TestApplication.GetCookie(rejectedRedirect, "__Host-plutus");
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", sessionCookie, StringComparison.OrdinalIgnoreCase);

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
        var sessionCookie = TestApplication.GetCookie(signIn, "__Host-plutus");
        var logoutToken = await app.GetLoginAsync(sessionCookie);

        var logout = await app.PostAsync(
            "/logout",
            new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("__RequestVerificationToken", logoutToken.Token)]),
            $"{sessionCookie}; {logoutToken.Cookie}");

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/login", logout.Headers.Location?.OriginalString);
        var clearedSession = TestApplication.GetCookie(logout, "__Host-plutus");
        Assert.Contains("expires=", clearedSession, StringComparison.OrdinalIgnoreCase);

        // Sign-out clears the browser's encrypted, stateless session cookie. A
        // client that honors that response no longer reaches protected routes.
        var afterLogout = await app.Client.GetAsync("/finance");
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
    }

    private sealed class TestApplication : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly CapturingLoggerProvider _loggerProvider;

        private TestApplication(WebApplication application, CapturingLoggerProvider loggerProvider, string password, string passwordHash)
        {
            _application = application;
            _loggerProvider = loggerProvider;
            Password = password;
            PasswordHash = passwordHash;
            Client = application.GetTestClient();
            Client.BaseAddress = new Uri("https://localhost");
        }

        public HttpClient Client { get; }
        public string Password { get; }
        public string PasswordHash { get; }
        public string Logs => _loggerProvider.Messages;

        public static async Task<TestApplication> StartAsync()
        {
            var password = $"test-{Guid.NewGuid():N}";
            var passwordHash = new PasswordHasher<object>().HashPassword(new object(), password);
            var loggerProvider = new CapturingLoggerProvider();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development",
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddSingleton<ILoggerProvider>(loggerProvider);
            builder.Services.AddDataProtection();
            builder.Services.AddAntiforgery();
            builder.Services.AddSingleAdministratorAuthentication();

            var application = builder.Build();
            application.UseRateLimiter();
            application.UseAuthentication();
            application.UseAuthorization();
            application.UseAntiforgery();
            application.MapSingleAdministratorAuthentication(passwordHash);
            application.MapGet("/finance", () => Results.Ok("protected finance data"));
            await application.StartAsync();

            return new TestApplication(application, loggerProvider, password, passwordHash);
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
            return new LoginToken(token, GetCookie(response, ".AspNetCore.Antiforgery"));
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

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync();
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
