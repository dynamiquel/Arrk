using System.Net;
using System.Text;

namespace Stackarr.Auth.Services;

/// <summary>
/// Shared chrome for the anonymous account pages (sign in, sign up). These are served as
/// plain HTML endpoints rather than Blazor components so they work before a circuit exists and
/// expose nothing except the form.
/// </summary>
public static class AuthPageHtml
{
    public static string Render(string title, string subtitle, string body)
    {
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1.0" />
                <title>{{H(title)}} - Stackarr</title>
                <link rel="preconnect" href="https://fonts.googleapis.com" />
                <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
                <link href="https://fonts.googleapis.com/css2?family=Google+Sans:wght@400;500;700&family=Inter:wght@400;500;600&display=swap" rel="stylesheet" />
                <style>
                    :root { color-scheme: dark; }
                    * { box-sizing: border-box; }
                    body {
                        margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
                        font-family: 'Google Sans', 'Product Sans', 'Inter', system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif;
                        background:
                            radial-gradient(circle at 18% 12%, rgba(192,132,252,.14) 0%, rgba(15,10,24,0) 42%),
                            radial-gradient(circle at 85% 88%, rgba(232,121,160,.12) 0%, rgba(15,10,24,0) 45%),
                            #0f0a18;
                        color: #e8e0f0; padding: 24px;
                        -webkit-font-smoothing: antialiased;
                        -moz-osx-font-smoothing: grayscale;
                    }
                    .card {
                        position: relative; width: 100%; max-width: 400px;
                        background: #181225; border: 1px solid #3a2a52;
                        border-radius: 14px; padding: 36px 32px; overflow: hidden;
                        box-shadow: 0 24px 60px rgba(0,0,0,.55), 0 0 30px rgba(192,132,252,.1);
                    }
                    /* Thin brand hairline along the top, matching the in-app header. */
                    .card::before {
                        content: ''; position: absolute; top: 0; left: 0; right: 0; height: 2px;
                        background: linear-gradient(90deg, #e879a0, #c084fc, #818cf8); opacity: .7;
                    }
                    .brand { display: flex; align-items: center; gap: 12px; margin-bottom: 6px; }
                    .logo {
                        width: 40px; height: 40px; border-radius: 11px;
                        background: linear-gradient(135deg, #e879a0, #c084fc);
                        box-shadow: 0 0 12px rgba(192,132,252,.2);
                        display: flex; align-items: center; justify-content: center; font-weight: 700; font-size: 20px; color: #fff;
                    }
                    h1 { font-size: 1.35rem; margin: 0; font-weight: 700; }
                    .sub { color: #a899be; font-size: .9rem; margin: 8px 0 26px; }
                    label { display: block; font-size: .78rem; text-transform: uppercase; letter-spacing: .08em; color: #a899be; margin-bottom: 6px; }
                    input {
                        width: 100%; padding: 12px 14px; margin-bottom: 18px; border-radius: 8px;
                        border: 1px solid #3a2a52; background: #0f0a18; color: #e8e0f0; font-size: .95rem; outline: none;
                        font-family: inherit;
                        transition: border-color .15s ease, box-shadow .15s ease;
                    }
                    input:focus { border-color: #c084fc; box-shadow: 0 0 0 3px rgba(192,132,252,.2); }
                    button, .sso {
                        width: 100%; padding: 12px 16px; border-radius: 8px; border: none; cursor: pointer;
                        font-size: .95rem; font-weight: 600; text-align: center; text-decoration: none; display: block;
                        font-family: inherit;
                        transition: box-shadow .15s ease, filter .15s ease, background .15s ease, transform .15s ease;
                    }
                    button {
                        background: linear-gradient(135deg, #e879a0, #c084fc);
                        color: white; margin-top: 4px;
                        box-shadow: 0 4px 14px rgba(0,0,0,.35);
                    }
                    button:hover { filter: brightness(1.05); box-shadow: 0 6px 20px rgba(0,0,0,.42), 0 0 16px rgba(192,132,252,.22); }
                    button:active { transform: translateY(1px); }
                    .sso { background: #221a30; color: #cfc0e0; border: 1px solid #3a2a52; margin-bottom: 10px; }
                    .sso:hover { background: #2a2040; border-color: #4a3668; box-shadow: 0 0 14px rgba(192,132,252,.18); }
                    .divider { display: flex; align-items: center; gap: 12px; color: #6b5c80; font-size: .75rem; margin: 22px 0; text-transform: uppercase; letter-spacing: .1em; }
                    .divider::before, .divider::after { content: ""; flex: 1; height: 1px; background: #2d2040; }
                    .error { background: rgba(248,113,113,.12); border: 1px solid rgba(248,113,113,.4); color: #f87171; padding: 10px 14px; border-radius: 8px; font-size: .85rem; margin-bottom: 18px; }
                    .notice { background: rgba(74,222,128,.1); border: 1px solid rgba(74,222,128,.35); color: #86efac; padding: 10px 14px; border-radius: 8px; font-size: .85rem; margin-bottom: 18px; }
                    .alt { margin-top: 22px; padding-top: 18px; border-top: 1px solid #2d2040; text-align: center; font-size: .85rem; color: #a899be; }
                    .alt a { color: #d8b4fe; text-decoration: none; font-weight: 600; }
                    .alt a:hover { text-decoration: underline; }
                </style>
            </head>
            <body>
                <div class="card">
                    <div class="brand">
                        <div class="logo">S</div>
                        <h1>Stackarr</h1>
                    </div>
                    <p class="sub">{{H(subtitle)}}</p>
                    {{body}}
                </div>
            </body>
            </html>
            """;
    }

    public static string H(string value) => WebUtility.HtmlEncode(value);

    public static string Error(string? message)
        => string.IsNullOrEmpty(message) ? "" : $"""<div class="error" role="alert">{H(message)}</div>""";

    public static string Notice(string? message)
        => string.IsNullOrEmpty(message) ? "" : $"""<div class="notice" role="status">{H(message)}</div>""";

    public static string Field(string id, string label, string type, string value = "", string autocomplete = "")
        => new StringBuilder()
            .Append($"""<label for="{H(id)}">{H(label)}</label>""")
            .Append($"""<input id="{H(id)}" name="{H(id)}" type="{H(type)}" value="{H(value)}" autocomplete="{H(autocomplete)}" />""")
            .ToString();
}
