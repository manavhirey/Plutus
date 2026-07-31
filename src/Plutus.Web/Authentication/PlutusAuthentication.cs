using System.Buffers.Binary;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace Plutus.Web.Authentication;

/// <summary>
/// Authentication for the sole administrator of a self-hosted Plutus instance.
/// The password hash is deliberately read only from the process environment, never
/// from a configuration file or the database.
/// </summary>
public static class PlutusAuthentication
{
    public const string PasswordHashEnvironmentVariable = "PLUTUS_AUTH_PASSWORD_HASH";
    public const string LoginPath = "/login";
    public const string LogoutPath = "/logout";
    internal const string LoginRateLimitPolicy = "plutus-login";

    private const string CookieName = "__Host-plutus";

    public static string GetRequiredPasswordHash(Func<string?>? getEnvironmentVariable = null)
    {
        var configuredHash = (getEnvironmentVariable ??
            (() => Environment.GetEnvironmentVariable(PasswordHashEnvironmentVariable)))();

        if (string.IsNullOrWhiteSpace(configuredHash) || !IsRecognizedAspNetPasswordHash(configuredHash))
        {
            throw new InvalidOperationException(
                $"Application startup aborted: {PasswordHashEnvironmentVariable} must be set to a valid ASP.NET password hash.");
        }

        return configuredHash;
    }

    public static void AddSingleAdministratorAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.Path = "/";
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
                options.LoginPath = LoginPath;
                options.AccessDeniedPath = LoginPath;
            });

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build());

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginRateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(15),
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
        });
    }

    public static IEndpointRouteBuilder MapSingleAdministratorAuthentication(this IEndpointRouteBuilder endpoints, string passwordHash)
    {
        endpoints.MapGet(LoginPath, (HttpContext context, IAntiforgery antiforgery, string? returnUrl) =>
            LoginPage(context, antiforgery, returnUrl, failed: false))
            .AllowAnonymous();

        endpoints.MapPost(LoginPath, async (HttpContext context, IAntiforgery antiforgery, ILoggerFactory loggerFactory) =>
        {
            if (!await IsAntiforgeryRequestValidAsync(context, antiforgery))
            {
                return Results.BadRequest();
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var password = form["password"].ToString();
            var returnUrl = form["returnUrl"].ToString();

            if (!VerifyPassword(passwordHash, password))
            {
                loggerFactory.CreateLogger("Plutus.Authentication")
                    .LogWarning("Rejected an invalid Plutus administrator login attempt.");
                return LoginPage(context, antiforgery, returnUrl, failed: true, StatusCodes.Status401Unauthorized);
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "administrator")],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    AllowRefresh = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
                });

            return Results.Redirect(SafeReturnUrl(returnUrl));
        })
        .AllowAnonymous()
        .RequireRateLimiting(LoginRateLimitPolicy);

        endpoints.MapPost(LogoutPath, async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await IsAntiforgeryRequestValidAsync(context, antiforgery))
            {
                return Results.BadRequest();
            }

            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect(LoginPath);
        });

        return endpoints;
    }

    internal static bool VerifyPassword(string passwordHash, string password)
    {
        try
        {
            return new PasswordHasher<object>().VerifyHashedPassword(new object(), passwordHash, password) is
                PasswordVerificationResult.Success or PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (ArgumentException)
        {
            // A malformed deployment value is never exposed through the login UI.
            return false;
        }
        catch (FormatException)
        {
            // A malformed deployment value is never exposed through the login UI.
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            // A malformed deployment value is never exposed through the login UI.
            return false;
        }
    }

    internal static string SafeReturnUrl(string? returnUrl) =>
        IsLocalReturnUrl(returnUrl) ? returnUrl! : "/";

    internal static bool IsLocalReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) &&
        returnUrl.StartsWith('/') &&
        !returnUrl.StartsWith("//", StringComparison.Ordinal) &&
        !returnUrl.StartsWith("/\\", StringComparison.Ordinal) &&
        !returnUrl.Contains('\\') &&
        Uri.TryCreate(returnUrl, UriKind.Relative, out _);

    private static bool IsRecognizedAspNetPasswordHash(string passwordHash)
    {
        try
        {
            var bytes = Convert.FromBase64String(passwordHash);
            if (bytes.Length == 0)
            {
                return false;
            }

            // ASP.NET Identity v2 hashes are exactly a format marker, 16-byte salt,
            // and 32-byte subkey. V3 hashes include PRF, iteration count, and salt
            // length as network-byte-order UInt32 values before salt and subkey.
            if (bytes[0] == 0x00)
            {
                return bytes.Length == 49;
            }

            if (bytes[0] != 0x01 || bytes.Length < 45)
            {
                return false;
            }

            var prf = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(1, 4));
            var iterations = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(5, 4));
            var saltLength = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9, 4));
            const int headerLength = 13;
            const int minimumSaltLength = 16;
            const int minimumSubkeyLength = 16;

            return prf <= 2 &&
                   iterations > 0 &&
                   saltLength >= minimumSaltLength &&
                   saltLength <= bytes.Length - headerLength - minimumSubkeyLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<bool> IsAntiforgeryRequestValidAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private static IResult LoginPage(
        HttpContext context,
        IAntiforgery antiforgery,
        string? returnUrl,
        bool failed,
        int statusCode = StatusCodes.Status200OK)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        var tokens = antiforgery.GetAndStoreTokens(context);
        return Results.Content(
            LoginPageHtml.Render(tokens.RequestToken ?? string.Empty, SafeReturnUrl(returnUrl), failed),
            "text/html; charset=utf-8",
            statusCode: statusCode);
    }

    private static class LoginPageHtml
    {
        public static string Render(string requestToken, string returnUrl, bool failed)
        {
            var encoder = HtmlEncoder.Default;
            var failureMessage = failed
                ? "<p class=\"error\" role=\"alert\">The password was not accepted.</p>"
                : string.Empty;

            return $$"""
                <!doctype html>
                <html lang="en">
                <head>
                  <meta charset="utf-8">
                  <meta name="viewport" content="width=device-width, initial-scale=1">
                  <meta name="robots" content="noindex,nofollow">
                  <title>Sign in — Plutus</title>
                  <style>
                    :root { color-scheme: light; font-family: system-ui, sans-serif; }
                    body { min-height: 100vh; display: grid; place-items: center; margin: 0; background: #f4f6f9; color: #1f2a40; }
                    main { width: min(24rem, calc(100% - 2rem)); box-sizing: border-box; background: white; padding: 2rem; border-radius: .75rem; box-shadow: 0 .5rem 2rem #1f2a4020; }
                    h1 { margin: 0 0 .5rem; font-size: 1.5rem; } p { color: #5f6b7a; } label { display: block; margin: 1.5rem 0 .5rem; font-weight: 600; }
                    input { box-sizing: border-box; width: 100%; padding: .75rem; border: 1px solid #aeb7c2; border-radius: .35rem; font: inherit; }
                    button { width: 100%; margin-top: 1.25rem; padding: .75rem; border: 0; border-radius: .35rem; background: #1f2a40; color: white; font: inherit; font-weight: 600; cursor: pointer; }
                    .error { padding: .75rem; border-radius: .35rem; background: #fff0f0; color: #9b1c1c; }
                  </style>
                </head>
                <body><main>
                  <h1>Sign in to Plutus</h1>
                  <p>Enter the administrator password to continue.</p>
                  {{failureMessage}}
                  <form method="post" action="/login">
                    <input type="hidden" name="__RequestVerificationToken" value="{{encoder.Encode(requestToken)}}">
                    <input type="hidden" name="returnUrl" value="{{encoder.Encode(returnUrl)}}">
                    <label for="password">Password</label>
                    <input id="password" name="password" type="password" autocomplete="current-password" required autofocus>
                    <button type="submit">Sign in</button>
                  </form>
                </main></body></html>
                """;
        }
    }
}
