using FlexivManager.Components.Pages;
using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace FlexivManager.Hosting;

/// <summary>
/// The operator host (localhost) serves only the operator panel. Any other page sends the browser to it:
/// a direct request (address bar, link, refresh) is redirected by <see cref="UseOperatorPagesOnly"/>,
/// an in-app navigation by the router guard in Routes.razor.
/// The read-only JSON API (/api/...) is not a page and stays available.
/// </summary>
public static class OperatorPages
{
    public const string Home = "/operator";

    // Error pages stay reachable: the pipeline re-executes them for errors and 404.
    private static readonly HashSet<Type> Allowed = [typeof(Operator), typeof(NotFound), typeof(Error)];

    // Route templates (@page) of every page in the app, and of the allowed ones.
    private static readonly HashSet<string> AllTemplates = Templates(typeof(OperatorPages).Assembly.GetTypes());
    private static readonly HashSet<string> AllowedTemplates = Templates(Allowed);

    public static bool IsAllowed(Type pageType) => Allowed.Contains(pageType);

    public static IApplicationBuilder UseOperatorPagesOnly(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        // Runs after routing (implicit in WebApplication): a page endpoint is recognised by its @page template.
        var template = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        if (template is not null && AllTemplates.Contains(template) && !AllowedTemplates.Contains(template))
        {
            context.Response.Redirect(Home);
            return;
        }
        await next(context);
    });

    private static HashSet<string> Templates(IEnumerable<Type> types) =>
        new(types.Where(t => typeof(IComponent).IsAssignableFrom(t))
                 .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
                 .Select(a => a.Template),
            StringComparer.OrdinalIgnoreCase);
}
