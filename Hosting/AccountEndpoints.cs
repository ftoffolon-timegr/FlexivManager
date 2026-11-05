using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using RobotGateway.Core;

namespace FlexivManager.Hosting;

/// <summary>
/// Single-account login for the planning pages (monitoring host only). Plain HTML + form post, so the
/// cookie is issued by a normal HTTP request (not over the Blazor circuit).
/// </summary>
public static class AccountEndpoints
{
    public const string LoginPath = "/account/login";
    public const string LogoutPath = "/account/logout";

    public static void MapAccountEndpoints(this WebApplication app, HostEndpointOptions options)
    {
        app.MapGet(LoginPath, (HttpContext ctx, IAntiforgery antiforgery, string? returnUrl) =>
            LoginPage(ctx, antiforgery, returnUrl, error: null, options));

        app.MapPost(LoginPath, async (
            HttpContext ctx,
            IAntiforgery antiforgery,
            EventLog events,
            TimeProvider time,
            [FromForm] string? userName,
            [FromForm] string? password,
            [FromForm] string? returnUrl) =>
        {
            var account = options.Editor;
            var valid = account.PasswordHash.Length > 0
                && string.Equals(userName?.Trim(), account.UserName, StringComparison.OrdinalIgnoreCase)
                && PasswordHasher.Verify(password ?? "", account.PasswordHash);
            var remote = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";

            if (!valid)
            {
                events.Add(new RobotEvent(time.GetUtcNow(), EventLevel.Warning, "Account", $"Accesso fallito per '{userName}' da {remote}."));
                await Task.Delay(TimeSpan.FromSeconds(1)); // slows down guessing
                var error = account.PasswordHash.Length == 0
                    ? "Nessuna password di modifica configurata: la pianificazione è in sola lettura."
                    : "Nome utente o password errati.";
                return LoginPage(ctx, antiforgery, returnUrl, error, options);
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, account.UserName), new Claim(ClaimTypes.Role, Policies.Planner)],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            events.Add(new RobotEvent(time.GetUtcNow(), EventLevel.Info, "Account", $"'{account.UserName}' ha effettuato l'accesso da {remote}."));
            return Results.LocalRedirect(SafeReturnUrl(returnUrl));
        });

        app.MapPost(LogoutPath, async (HttpContext ctx, EventLog events, TimeProvider time) =>
        {
            var name = ctx.User.Identity?.Name;
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (name is not null)
                events.Add(new RobotEvent(time.GetUtcNow(), EventLevel.Info, "Account", $"'{name}' è uscito."));
            return Results.LocalRedirect("/");
        }).WithMetadata(new RequireAntiforgeryTokenAttribute());
    }

    private static string SafeReturnUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";

    private static IResult LoginPage(HttpContext ctx, IAntiforgery antiforgery, string? returnUrl, string? error, HostEndpointOptions options)
    {
        var tokens = antiforgery.GetAndStoreTokens(ctx);
        var e = (string? s) => WebUtility.HtmlEncode(s ?? "");
        var html = $$"""
            <!DOCTYPE html>
            <html lang="it" data-bs-theme="dark">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>Accesso pianificazione</title>
              <link rel="stylesheet" href="/lib/bootstrap/dist/css/bootstrap.min.css" />
            </head>
            <body class="d-flex align-items-center justify-content-center" style="min-height:100vh">
              <form method="post" action="{{LoginPath}}" class="card p-4" style="width:22rem">
                <h1 class="h4 mb-3">Accesso pianificazione</h1>
                <p class="small text-muted">Serve per modificare i codici lavoro e gli ordini di produzione. Le pagine di monitoraggio restano aperte.</p>
                {{(error is null ? "" : $"<div class=\"alert alert-danger py-2\">{e(error)}</div>")}}
                <input type="hidden" name="{{e(tokens.FormFieldName)}}" value="{{e(tokens.RequestToken)}}" />
                <input type="hidden" name="returnUrl" value="{{e(SafeReturnUrl(returnUrl))}}" />
                <label class="form-label" for="userName">Utente</label>
                <input class="form-control mb-2" id="userName" name="userName" value="{{e(options.Editor.UserName)}}" autocomplete="username" />
                <label class="form-label" for="password">Password</label>
                <input class="form-control mb-3" id="password" name="password" type="password" autocomplete="current-password" autofocus />
                <button class="btn btn-primary w-100" type="submit">Accedi</button>
                <a class="small mt-3" href="/">Indietro</a>
              </form>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
