using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace BidBuilder.Api.Middleware;

/// <summary>
/// 29.A.4 — Security headers on every API response.
/// </summary>
/// <remarks>
/// <para>
/// The API serves JSON to the SPA and a small set of HTML surfaces
/// (Swagger UI in Development, the Hangfire dashboard at /hangfire when
/// enabled). Headers are written once on <c>OnStarting</c> so they survive
/// the IExceptionHandler short-circuit too — an unhandled 500 with no
/// security headers would be exactly the wrong default.
/// </para>
/// <para>
/// CSP is the variable one: Swagger UI ships an inline boot script and
/// pulls fonts/CSS from swagger-ui-dist (served from our own origin in
/// Development), so paths under <c>/swagger</c> get a relaxed policy.
/// The Hangfire dashboard is the same shape. Every OTHER path — every
/// JSON endpoint, the OpenAPI document, /healthz, /metrics — gets the
/// strict policy: nothing loads at all (<c>default-src 'none'</c>), no
/// origin can frame the response, no MIME-type sniffing.
/// </para>
/// <para>
/// HSTS is set unconditionally so a tenant that points a custom domain at
/// the API origin still gets transport hardening — in front of Caddy the
/// header is redundant but harmless, and direct/test deployments without
/// the reverse proxy are covered.
/// </para>
/// </remarks>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IWebHostEnvironment _env;

    public SecurityHeadersMiddleware(RequestDelegate next, IWebHostEnvironment env)
    {
        _next = next;
        _env = env;
    }

    public Task InvokeAsync(HttpContext ctx)
    {
        // Capture the environment + path before yielding to downstream — the
        // path can be rewritten by routing, and we want what the client asked
        // for (e.g. /swagger), not what was rewritten to.
        var path = ctx.Request.Path.Value ?? string.Empty;
        var isHtmlSurface =
            path.StartsWith("/swagger", System.StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/hangfire", System.StringComparison.OrdinalIgnoreCase);

        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;

            // Frame-ancestors on a JSON API is the load-bearing one: prevents
            // any site from iframing an error page that happens to render
            // user-controllable text. DENY for JSON, 'none' via CSP for HTML.
            h["X-Frame-Options"] = "DENY";
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            // Deny powerful sensors by default — no API surface uses them.
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            // 1y + includeSubDomains + preload meets the HSTS preload-list bar.
            h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains; preload";

            if (isHtmlSurface)
            {
                // Swagger UI / Hangfire need to render inline scripts + load
                // their own assets. Both ship from our origin once mapped, so
                // self-only is correct and we don't widen further.
                h["Content-Security-Policy"] =
                    "default-src 'self'; " +
                    "script-src 'self' 'unsafe-inline'; " +
                    "style-src 'self' 'unsafe-inline'; " +
                    "img-src 'self' data:; " +
                    "font-src 'self' data:; " +
                    "connect-src 'self'; " +
                    "frame-ancestors 'none'; " +
                    "base-uri 'self'; " +
                    "form-action 'self'";
            }
            else
            {
                // JSON / health / metrics / OpenAPI doc: nothing should ever
                // load anything. If a browser somehow ends up rendering this
                // response, no scripts, no styles, no images.
                h["Content-Security-Policy"] =
                    "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
            }

            return Task.CompletedTask;
        });

        return _next(ctx);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
        => app.UseMiddleware<SecurityHeadersMiddleware>();
}
