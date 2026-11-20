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
        :root{color-scheme:light dark;--bg:#f1f6fa;--card:#fff;--text:#0f2233;--muted:#5a6b7a;--line:#cfdbe5;--accent:#0b6fa4;--accent-text:#fff;--field:#fff;--glow:#bae6fd;font-family:system-ui,-apple-system,"Segoe UI",sans-serif}
        @media (prefers-color-scheme:dark){:root{--bg:#10151b;--card:#1a212a;--text:#e8eff5;--muted:#9aabba;--line:#2f3b48;--accent:#38b2e8;--accent-text:#04202f;--field:#121920;--glow:#0c4a6e}}
        *{box-sizing:border-box}
        body{margin:0;display:grid;min-height:100vh;place-items:center;padding:1rem;color:var(--text);background:radial-gradient(60rem 40rem at 50% -10%,var(--glow),transparent 70%),var(--bg)}
        main{width:min(26rem,100%);padding:2rem 1.75rem 1.75rem;background:var(--card);border:1px solid var(--line);border-radius:1rem;box-shadow:0 .5rem 2rem rgb(8 47 73/.14)}
        .brand{display:flex;align-items:center;gap:.75rem;margin-bottom:1.5rem}
        .brand svg{width:2.75rem;height:2.75rem;flex:none;border-radius:.7rem}
        .brand span{font-size:1.35rem;font-weight:700;letter-spacing:.01em}
        h1{font-size:1.4rem;margin:0 0 .4rem}
        p{line-height:1.5;margin:.4rem 0;color:var(--muted)}
        p strong{color:var(--text)}
        label{display:block;font-weight:600;font-size:.9rem;margin:1.1rem 0 .35rem}
        input[type=email],input[type=text]{width:100%;padding:.7rem .8rem;font:inherit;color:var(--text);background:var(--field);border:1px solid var(--line);border-radius:.6rem}
        input#code{font-size:1.5rem;letter-spacing:.4em;text-align:center;font-variant-numeric:tabular-nums}
        button,.provider{display:block;width:100%;margin-top:1rem;padding:.75rem;font:inherit;font-weight:600;text-align:center;text-decoration:none;border-radius:.6rem;border:1px solid var(--line);background:transparent;color:var(--text);cursor:pointer}
        button:hover,.provider:hover{border-color:var(--accent)}
        button.primary{background:var(--accent);color:var(--accent-text);border-color:var(--accent)}
        button.primary:hover{filter:brightness(1.08)}
        .alert{padding:.65rem .85rem;border-radius:.5rem;border-left:.3rem solid #c0392b;background:color-mix(in srgb,#c0392b 14%,var(--card));color:var(--text)}
        .note{font-size:.9rem}
        a{color:var(--accent)}
        hr{margin:1.5rem 0 1rem;border:0;border-top:1px solid var(--line)}
        :focus-visible{outline:3px solid var(--accent);outline-offset:2px}
        """;

    /// <summary>The app icon (eng/assets/chaos-icon.svg), inline so the page needs no other request.</summary>
    private const string Logo = """<svg class="brand-mark" viewBox="0 0 256 256" role="img" aria-label="Chaos logo" xmlns="http://www.w3.org/2000/svg"><defs><linearGradient id="tile" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#0EA5E9"/><stop offset="1" stop-color="#075985"/></linearGradient></defs><rect width="256" height="256" rx="56.32" fill="url(#tile)"/><g transform="translate(33.28 40.86) scale(1.8944)" fill="none" stroke-linecap="round" stroke-linejoin="round" stroke-width="14.9"><path transform="translate(0 3.4)" stroke="#082F49" stroke-opacity="0.35" d="M10 64 C26 60 46 52 55 36 C62 22 54 8 40 12 C26 17 26 44 40 78 C52 60 68 38 90 16"/><path stroke="#FFFFFF" d="M10 64 C26 60 46 52 55 36 C62 22 54 8 40 12 C26 17 26 44 40 78 C52 60 68 38 90 16"/><g fill="#BAE6FD" fill-opacity="0.92" stroke="none"><circle cx="78" cy="78" r="4.5"/><circle cx="64" cy="90" r="3"/><circle cx="91" cy="62" r="2.5"/></g></g></svg>""";

    public static IResult EmailPage(HttpContext http, IAntiforgery antiforgery, IReadOnlyList<ExternalProviderInfo> providers, string? returnUrl, string? error = null, int status = 200)
    {
        var body = new StringBuilder();
        body.Append("<h1>Sign in</h1><p>Sync your tasks and notes across your devices.</p>");
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

    public static IResult InviteLanding(string returnUrl)
    {
        var body = "<h1>A Chaos invitation</h1><p>Sign in to see which space invited you.</p>"
            + $"<a class=\"provider\" href=\"/signin?ReturnUrl={Uri.EscapeDataString(returnUrl)}\">Sign in</a>";
        return Render("Chaos invitation", body, 200);
    }

    public static IResult InviteJoin(HttpContext http, Guid accountId, string token, string spaceName, string inviter)
    {
        var body = new StringBuilder();
        body.Append($"<h1>Join {Encode(spaceName)}</h1>");
        body.Append($"<p>{Encode(inviter)} invited you to this Chaos space.</p>");
        body.Append($"<form method=\"post\" action=\"/invite/{Encode(token)}\">");
        var antiforgery = http.RequestServices.GetRequiredService<Microsoft.AspNetCore.Antiforgery.IAntiforgery>();
        var tokens = antiforgery.GetAndStoreTokens(http);
        body.Append($"<input type=\"hidden\" name=\"{Encode(tokens.FormFieldName)}\" value=\"{Encode(tokens.RequestToken ?? string.Empty)}\">");
        body.Append("<button class=\"primary\" type=\"submit\">Join space</button></form>");
        return Render("Join " + spaceName, body.ToString(), 200);
    }

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
        var html = $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>{Encode(title)} - Chaos</title><style>{Style}</style></head><body><main><div class=\"brand\">{Logo}<span>Chaos</span></div>{body}</main></body></html>";
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);
    }
}
