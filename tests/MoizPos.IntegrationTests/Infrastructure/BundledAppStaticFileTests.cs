using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace MoizPos.IntegrationTests.Infrastructure;

/// <summary>
/// Serving the counter app and product pictures from the API, with a bundled <c>wwwroot</c> —
/// the shape the shop's server actually runs in.
///
/// <para><b>Why this exists.</b> Every other test in this suite runs against a host with NO
/// wwwroot, because the test factory gives each run an empty temporary content root. With no
/// <c>index.html</c> present, <c>Program.cs</c> registers neither the SPA fallback nor the
/// wwwroot static files — so the deployed pipeline was never exercised, and a bug that made the
/// entire deployed app unusable sat behind a green suite.</para>
///
/// <para><b>The bug.</b> Minimal hosting inserts <c>UseRouting()</c> at the START of the pipeline
/// when it is not called explicitly. <c>MapFallbackToFile("{*path}")</c> then selected an
/// endpoint for every path that is not <c>/api</c> — and <c>StaticFileMiddleware</c> deliberately
/// skips a request that already has an endpoint. So EVERY static file answered
/// <c>200 OK, text/html</c> with the contents of index.html: product pictures, the app's own
/// JavaScript and CSS, the favicon. The page loaded and then did nothing, and every photograph
/// fell back to the placeholder.</para>
///
/// <para>The fix is one line — <c>app.UseRouting()</c> placed after the static-file middlewares,
/// so files get their chance before endpoint selection. These tests fail if it moves back.</para>
/// </summary>
public sealed class BundledAppStaticFileTests : IAsyncLifetime
{
    private readonly ApiFactory _seed = new();
    private BundledHost _host = null!;
    private string _root = string.Empty;

    public async Task InitializeAsync()
    {
        // Borrowed only for its connection string: the host below needs a migrated database to
        // start, and standing up a second one would double the suite's slowest step.
        await _seed.InitializeAsync();

        _root = Path.Combine(Path.GetTempPath(), $"moizpos-bundled-{Guid.NewGuid():N}");

        // The deployed layout: a built client in wwwroot, and pictures under content/products.
        Directory.CreateDirectory(Path.Combine(_root, "wwwroot", "assets"));
        Directory.CreateDirectory(Path.Combine(_root, "content", "products"));

        await File.WriteAllTextAsync(
            Path.Combine(_root, "wwwroot", "index.html"),
            "<!doctype html><html><body>counter app</body></html>");

        await File.WriteAllTextAsync(
            Path.Combine(_root, "wwwroot", "assets", "app.js"),
            "export const counter = 1;");

        await File.WriteAllTextAsync(
            Path.Combine(_root, "wwwroot", "assets", "app.css"),
            ".counter { color: red; }");

        // A real PNG header, so the served content type is decided by the extension rather than
        // by anything guessing at the bytes.
        await File.WriteAllBytesAsync(
            Path.Combine(_root, "content", "products", "photo.png"),
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        _host = new BundledHost(_root, _seed.ConnectionString);
        _ = _host.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _seed.DisposeAsync();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class BundledHost : WebApplicationFactory<Program>
    {
        private readonly string _contentRoot;
        private readonly string _connectionString;

        public BundledHost(string contentRoot, string connectionString)
        {
            _contentRoot = contentRoot;
            _connectionString = connectionString;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseContentRoot(_contentRoot);
            builder.UseWebRoot(Path.Combine(_contentRoot, "wwwroot"));

            // Host configuration, so it is in place before Program.cs resolves the connection —
            // and so a developer's user-secrets cannot point this at the live shop database.
            builder.UseSetting("ConnectionStrings:Default", _connectionString);
            builder.UseSetting("Database:SkipMachineLocalSettings", "true");
            builder.UseSetting("Database:EnforceStrictSqlMode", "true");
            builder.UseSetting("Jwt:Key", new string('k', 64));
        }
    }

    // ---------------------------------------------------------------- the files

    [Fact]
    public async Task A_product_picture_is_served_as_an_image_not_as_the_html_page()
    {
        var response = await _host.CreateClient().GetAsync("/content/products/photo.png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // THE assertion. A 200 alone passed all through the bug — it was 200 text/html, the
        // index page wearing a picture's URL. The browser showed a broken image and the screen
        // quietly fell back to the placeholder.
        response.Content.Headers.ContentType?.MediaType.Should().Be(
            "image/png", "a picture must arrive as a picture, not as index.html");
    }

    [Fact]
    public async Task The_counter_apps_own_javascript_is_served_as_javascript()
    {
        var response = await _host.CreateClient().GetAsync("/assets/app.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // With the bug this returned index.html, so the browser parsed HTML as a module, the
        // app never started, and the page sat blank — while the server reported 200 throughout.
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/javascript");
    }

    [Fact]
    public async Task A_stylesheet_in_wwwroot_is_served_as_a_stylesheet()
    {
        var response = await _host.CreateClient().GetAsync("/assets/app.css");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/css");
    }

    // ---------------------------------------------------------------- and the fallback still works

    [Fact]
    public async Task A_client_side_route_still_returns_the_app()
    {
        // The fallback is why refreshing on /pos does not 404. Fixing the static files must not
        // cost this — it is the whole reason the fallback is registered.
        var response = await _host.CreateClient().GetAsync("/pos");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task A_missing_picture_is_a_404_and_never_the_html_page()
    {
        var response = await _host.CreateClient().GetAsync("/content/products/gone.png");

        // "The file is not on this server" must be distinguishable from "here is your picture".
        // Answering with index.html made a deployment holding the database rows but none of the
        // image files look perfectly healthy: 200 OK on every request, placeholder on every
        // screen, and nothing anywhere saying why.
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html");
    }

    [Fact]
    public async Task An_unknown_api_path_never_answers_with_the_html_page()
    {
        // The fallback's regex excludes /api on purpose: without it a mistyped API path returns
        // the page with a 200, and a client bug reads as a working screen. (Unauthenticated it
        // is refused before routing can 404 it — either way, never HTML.)
        var response = await _host.CreateClient().GetAsync("/api/not-a-real-route");

        response.IsSuccessStatusCode.Should().BeFalse();
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/html");
    }
}
