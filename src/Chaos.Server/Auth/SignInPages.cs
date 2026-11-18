using System.Net;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;

namespace Chaos.Server.Auth;

/// <summary>
/// The server-rendered sign-in pages: plain HTML, no scripts, no third-party resources, labelled fields and
/// polite alerts. English for now (PLAN2 P12 item 3). Every dynamic value is HTML-encoded.
/// </summary>
internal static class SignInPages
{
    private const string Style = """
        :root{color-scheme:light dark;font-family:system-ui,sans-serif}
        body{margin:0;display:grid;min-height:100vh;place-items:center;background:Canvas;color:CanvasText}
        main{box-sizing:border-box;width:min(26rem,100%);padding:2rem 1.5rem}
        h1{font-size:1.5rem;margin:0 0 .5rem}
        p{line-height:1.5}
        label{display:block;font-weight:600;margin:1rem 0 .25rem}
        input[type=email],input[type=text]{box-sizing:border-box;width:100%;padding:.6rem;font:inherit;border:1px solid GrayText;border-radius:.4rem}
        button,.provider{display:block;box-sizing:border-box;width:100%;margin-top:1rem;padding:.65rem;font:inherit;font-weight:600;text-align:center;text-decoration:none;border-radius:.4rem;border:1px solid GrayText;background:ButtonFace;color:ButtonText;cursor:pointer}
        button.primary{background:Highlight;color:HighlightText;border-color:Highlight}
        .alert{padding:.6rem .8rem;border-left:.3rem solid #c0392b;background:color-mix(in srgb,#c0392b 12%,Canvas)}
        .note{color:GrayText;font-size:.9rem}
        hr{margin:1.5rem 0;border:0;border-top:1px solid GrayText}
        :focus-visible{outline:3px solid Highlight;outline-offset:2px}
        """;

    public static IResult EmailPage(HttpContext http, IAntiforgery antiforgery, IReadOnlyList<ExternalProviderInfo> providers, string? returnUrl, string? error = null, int status = 200)
    {
        var body = new StringBuilder();
        body.Append("<h1>Sign in to Chaos</h1><p>Sync with your other devices.</p>");
        AppendError(body, error);
        body.Append("<form method=\"post\" action=\"/signin/email\">");
        AppendHidden(body, http, antiforgery, returnUrl);
        body.Append("<label for=\"email\">Email address</label><input id=\"email\" name=\"email\" type=\"email\" autocomplete=\"email\" required maxlength=\"254\" autofocus>");
        body.Append("<button class=\"primary\" type=\"submit\">Email me a code</button></form>");
        if (providers.Count > 0)
        {
            body.Append("<hr><p class=\"note\">Or continue with</p>");
            foreach (var provider in providers)
            {
                var link = $"/signin/external/{provider.Key}" + (returnUrl is null ? string.Empty : "?ReturnUrl=" + Uri.EscapeDataString(returnUrl));
                body.Append($"<a class=\"provider\" href=\"{Encode(link)}\">{Encode(provider.Display)}</a>");
            }
        }

        return Render("Sign in", body.ToString(), status);
    }

    public static IResult CodePage(HttpContext http, IAntiforgery antiforgery, string email, string? returnUrl, string? notice = null, string? error = null, int status = 200)
    {
        var body = new StringBuilder();
        body.Append("<h1>Enter your code</h1>");
        body.Append($"<p>{Encode(notice ?? "We sent a 6-digit code to")} <strong>{Encode(email)}</strong>. It works for 10 minutes.</p>");
        AppendError(body, error);
        body.Append("<form method=\"post\" action=\"/signin/verify\">");
        AppendHidden(body, http, antiforgery, returnUrl);
        body.Append($"<input type=\"hidden\" name=\"email\" value=\"{Encode(email)}\">");
        body.Append("<label for=\"code\">6-digit code</label><input id=\"code\" name=\"code\" type=\"text\" inputmode=\"numeric\" autocomplete=\"one-time-code\" pattern=\"[0-9]{6}\" required maxlength=\"6\" autofocus>");
        body.Append("<button class=\"primary\" type=\"submit\">Sign in</button></form>");
        body.Append("<p class=\"note\"><a href=\"/signin" + (returnUrl is null ? string.Empty : "?ReturnUrl=" + Encode(Uri.EscapeDataString(returnUrl))) + "\">Use a different email address</a></p>");
        return Render("Enter your code", body.ToString(), status);
    }

    public static IResult MessagePage(string title, string message, int status = 200) =>
        Render(title, $"<h1>{Encode(title)}</h1><p role=\"status\">{Encode(message)}</p>", status);

    private static void AppendHidden(StringBuilder body, HttpContext http, IAntiforgery antiforgery, string? returnUrl)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        body.Append($"<input type=\"hidden\" name=\"{Encode(tokens.FormFieldName)}\" value=\"{Encode(tokens.RequestToken ?? string.Empty)}\">");
        if (returnUrl is not null)
        {
            body.Append($"<input type=\"hidden\" name=\"returnUrl\" value=\"{Encode(returnUrl)}\">");
        }
    }

    private static void AppendError(StringBuilder body, string? error)
    {
        if (error is not null)
        {
            body.Append($"<p class=\"alert\" role=\"alert\">{Encode(error)}</p>");
        }
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static IResult Render(string title, string body, int status)
    {
        var html = $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{Encode(title)} - Chaos</title><style>{Style}</style></head><body><main>{body}</main></body></html>";
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
    }
}
