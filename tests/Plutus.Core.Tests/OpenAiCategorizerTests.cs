using System.Net;
using System.ClientModel;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using OpenAI.Responses;
using Plutus.Core;
using Plutus.Core.Categorization;
using Plutus.Core.Models;

namespace Plutus.Core.Tests;

public sealed class OpenAiCategorizerTests
{
    private static readonly object EnvironmentLock = new();

    private static readonly List<Category> Categories =
    [
        new() { Id = 1, Name = "Groceries" },
        new() { Id = 2, Name = "Dining" },
        new() { Id = 3, Name = "Transport" },
    ];

    [Fact]
    public void BuildSchema_constrains_category_and_confidence()
    {
        using var schema = JsonDocument.Parse(OpenAiCategorizer.BuildSchema(Categories));
        var properties = schema.RootElement.GetProperty("properties");
        var enumValues = properties.GetProperty("category").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(new[] { "Groceries", "Dining", "Transport" }, enumValues);
        Assert.Equal(0, properties.GetProperty("confidence").GetProperty("minimum").GetDouble());
        Assert.Equal(1, properties.GetProperty("confidence").GetProperty("maximum").GetDouble());
        Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task CategorizeAsync_sends_a_private_strict_responses_request_and_parses_valid_output()
    {
        await using var server = new CapturedResponseServer();
        var responseTask = server.RespondAsync(ResponseWithOutput("{\"category\":\"Groceries\",\"note\":\"Fresh groceries\",\"confidence\":0.75}"));
        var categorizer = CreateCategorizer(server.Endpoint, "configured-model");

        var result = await categorizer.CategorizeAsync("MARKET 123", null, Categories);
        var request = await responseTask;

        Assert.Equal(new CategorizationResult("Groceries", "Fresh groceries", 0.75), result);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/v1/responses", request.Path);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("configured-model", body.RootElement.GetProperty("model").GetString());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal(256, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("low", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());

        var format = body.RootElement.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        var categoryEnum = format.GetProperty("schema").GetProperty("properties")
            .GetProperty("category").GetProperty("enum").EnumerateArray().Select(x => x.GetString());
        Assert.Equal(Categories.Select(x => x.Name), categoryEnum);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"category\":\"Groceries\",\"note\":\"Market\",\"confidence\":1.1}")]
    [InlineData("{\"category\":\"Unknown\",\"note\":\"Market\",\"confidence\":0.5}")]
    public async Task CategorizeAsync_returns_null_for_invalid_or_unknown_structured_output(string output)
    {
        await using var server = new CapturedResponseServer();
        var responseTask = server.RespondAsync(ResponseWithOutput(output));

        var result = await CreateCategorizer(server.Endpoint).CategorizeAsync("MARKET", null, Categories);

        await responseTask;
        Assert.Null(result);
    }

    [Fact]
    public async Task CategorizeAsync_returns_null_for_a_refusal()
    {
        await using var server = new CapturedResponseServer();
        var responseTask = server.RespondAsync(ResponseWithRefusal());

        var result = await CreateCategorizer(server.Endpoint).CategorizeAsync("MARKET", null, Categories);

        await responseTask;
        Assert.Null(result);
    }

    [Fact]
    public async Task CategorizeAsync_returns_null_for_a_provider_error()
    {
        await using var server = new CapturedResponseServer();
        var responseTask = server.RespondAsync("{\"error\":{\"message\":\"invalid request\"}}", statusCode: 400);

        var result = await CreateCategorizer(server.Endpoint).CategorizeAsync("MARKET", null, Categories);

        await responseTask;
        Assert.Null(result);
    }

    [Fact]
    public async Task CategorizeAsync_preserves_cancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var server = new CapturedResponseServer();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateCategorizer(server.Endpoint).CategorizeAsync("MARKET", null, Categories, cancelled.Token));
    }

    [Fact]
    public void NormalizeNote_returns_null_for_null_empty_and_whitespace()
    {
        Assert.Null(OpenAiCategorizer.NormalizeNote(null));
        Assert.Null(OpenAiCategorizer.NormalizeNote(""));
        Assert.Null(OpenAiCategorizer.NormalizeNote("   "));
        Assert.Equal("Coffee", OpenAiCategorizer.NormalizeNote("  Coffee  "));
    }

    [Fact]
    public void AddPlutusCore_binds_the_configured_model()
    {
        lock (EnvironmentLock)
        {
            WithOpenAiApiKey("test-key", () =>
            {
                var configuration = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Plutus:OpenAI:Model"] = "configured-model",
                    })
                    .Build();

                using var provider = new ServiceCollection()
                    .AddPlutusCore(configuration, "Data Source=:memory:")
                    .BuildServiceProvider();

                Assert.Equal("configured-model", provider.GetRequiredService<IOptions<OpenAiOptions>>().Value.Model);
            });
        }
    }

    [Fact]
    public void AddPlutusCore_reports_a_clear_missing_key_error()
    {
        lock (EnvironmentLock)
        {
            WithOpenAiApiKey(null, () =>
            {
                var configuration = new ConfigurationBuilder().Build();
                var exception = Assert.Throws<InvalidOperationException>(() =>
                    new ServiceCollection().AddPlutusCore(configuration, "Data Source=:memory:"));

                Assert.Contains("OPENAI_API_KEY is not set", exception.Message, StringComparison.Ordinal);
            });
        }
    }

    private static OpenAiCategorizer CreateCategorizer(Uri endpoint, string model = "gpt-5.6-luna") =>
        new(
            new ResponsesClient(new ApiKeyCredential("test-key"), new ResponsesClientOptions { Endpoint = endpoint }),
            Options.Create(new OpenAiOptions { Model = model }),
            NullLogger<OpenAiCategorizer>.Instance);

    private static void WithOpenAiApiKey(string? value, Action action)
    {
        var original = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", value);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", original);
        }
    }

    private static string ResponseWithOutput(string text) => JsonSerializer.Serialize(new
    {
        id = "resp_test",
        @object = "response",
        created_at = 0,
        status = "completed",
        model = "test",
        output = new[]
        {
            new
            {
                id = "msg_test",
                type = "message",
                status = "completed",
                role = "assistant",
                content = new[] { new { type = "output_text", text, annotations = Array.Empty<object>() } },
            },
        },
        parallel_tool_calls = true,
        tools = Array.Empty<object>(),
        tool_choice = "auto",
    });

    private static string ResponseWithRefusal() => JsonSerializer.Serialize(new
    {
        id = "resp_test",
        @object = "response",
        created_at = 0,
        status = "completed",
        model = "test",
        output = new[]
        {
            new
            {
                id = "msg_test",
                type = "message",
                status = "completed",
                role = "assistant",
                content = new[] { new { type = "refusal", refusal = "Unable to classify" } },
            },
        },
        parallel_tool_calls = true,
        tools = Array.Empty<object>(),
        tool_choice = "auto",
    });

    private sealed class CapturedResponseServer : IAsyncDisposable
    {
        private readonly HttpListener listener = new();

        public CapturedResponseServer()
        {
            var port = GetFreePort();
            Endpoint = new Uri($"http://127.0.0.1:{port}/v1/");
            listener.Prefixes.Add(Endpoint.ToString());
            listener.Start();
        }

        public Uri Endpoint { get; }

        public async Task<CapturedRequest> RespondAsync(string responseBody, int statusCode = 200)
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();

            var bytes = Encoding.UTF8.GetBytes(responseBody);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();

            return new CapturedRequest(context.Request.HttpMethod, context.Request.Url!.AbsolutePath, body);
        }

        public ValueTask DisposeAsync()
        {
            listener.Close();
            return ValueTask.CompletedTask;
        }

        private static int GetFreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }

    private sealed record CapturedRequest(string Method, string Path, string Body);
}
