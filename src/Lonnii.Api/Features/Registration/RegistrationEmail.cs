using System.Net;

namespace Lonnii.Api.Features.Registration;

/// <summary>
/// The confirmation email. Same look, wording and subject as the one Lonnii Business sends
/// (<c>utils/emailTemplates.js</c> there), so a customer who already uses the web app sees the
/// same message from the same sender.
/// </summary>
public static class RegistrationEmail
{
    public const string Subject = "Code de vérification de votre compte";

    // The logo the web app's emails already use.
    private const string LogoUrl = "https://www.lonnii.com/images/logo.png";

    public static string Html(string shopName, string code, int validMinutes) => $$"""
        <!DOCTYPE html>
        <html>
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1.0">
          <title>Vérification de votre compte</title>
          <style>
            body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; background-color: #f4f4f4; margin: 0; padding: 0; }
            .container { max-width: 600px; margin: 20px auto; background-color: #ffffff; border-radius: 12px; overflow: hidden; box-shadow: 0 4px 20px rgba(0,0,0,0.05); }
            .header { background: linear-gradient(135deg, #2563eb 0%, #1d4ed8 100%); padding: 30px 20px; text-align: center; }
            .header img { max-height: 60px; max-width: 180px; object-fit: contain; }
            .content { padding: 40px 30px; color: #334155; }
            .h1 { font-size: 24px; font-weight: 700; margin-bottom: 20px; color: #0f172a; }
            .p { font-size: 16px; line-height: 1.6; margin-bottom: 20px; color: #475569; }
            .code-box { background-color: #f8fafc; border: 2px dashed #cbd5e1; border-radius: 12px; padding: 25px; text-align: center; margin: 30px 0; }
            .code { font-size: 36px; font-weight: 800; letter-spacing: 8px; color: #2563eb; font-family: 'Courier New', Courier, monospace; }
            .footer { background-color: #f8fafc; padding: 20px; text-align: center; font-size: 13px; color: #94a3b8; border-top: 1px solid #e2e8f0; }
            .highlight { color: #2563eb; font-weight: 600; }
          </style>
        </head>
        <body>
          <div class="container">
            <div class="header">
              <img src="{{LogoUrl}}" alt="Lonnii" style="filter: brightness(0) invert(1);">
            </div>
            <div class="content">
              <div class="h1">Bonjour,</div>
              <div class="p">Merci de vous être inscrit sur Lonnii. Utilisez le code ci-dessous pour confirmer l'inscription de « {{WebUtility.HtmlEncode(shopName)}} ».</div>
              <div class="code-box">
                <div class="code">{{code}}</div>
              </div>
              <div class="p">Ce code est valable pendant <span class="highlight">{{validMinutes}} minutes</span>.</div>
              <div class="p">Si vous n'êtes pas à l'origine de cette demande, veuillez ignorer cet email. Aucun compte n'est créé sans ce code.</div>
              <div class="p" style="margin-top: 40px; border-top: 1px solid #e2e8f0; padding-top: 20px;">
                Cordialement,<br>
                <strong>L'équipe Lonnii</strong>
              </div>
            </div>
            <div class="footer">
              &copy; {{DateTime.UtcNow.Year}} Lonnii. Tous droits réservés.<br>
              Ceci est un message automatique, merci de ne pas y répondre.
            </div>
          </div>
        </body>
        </html>
        """;
}
