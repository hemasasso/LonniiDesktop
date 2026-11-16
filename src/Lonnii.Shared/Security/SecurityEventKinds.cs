namespace Lonnii.Shared.Security;

/// <summary>
/// Signs that someone is trying to get round a shop's licence, as a shop's computer notices them
/// (and reports them at its next check-in) or as our server does. Shown in the dashboard and in the
/// morning report, so we hear about an attempt while the shop is still online - not weeks later when
/// it has gone quiet.
///
/// <para>Each kind is recorded at most once a day per espace: a till retrying every few minutes must
/// not bury the dashboard.</para>
/// </summary>
public static class SecurityEventKinds
{
    // Noticed by the shop's computer.
    public const string LicenceOlder = "licence-older";
    public const string LicenceMissing = "licence-missing";
    public const string LicenceInvalid = "licence-invalid";
    public const string LicenceReplay = "licence-replay";
    public const string ClockBack = "clock-back";
    public const string ServerUrlChanged = "server-url-changed";
    public const string ConfigTampered = "config-tampered";
    public const string ModeEdited = "mode-edited";
    public const string PhoneOnlySet = "phone-only-set";
    public const string UnknownDevice = "unknown-device";

    // Noticed by our server.
    public const string RevokedDevice = "revoked-device";
    public const string UnboundDevice = "unbound-device";
    public const string HardwareChanged = "hardware-changed";
    public const string BinariesModified = "binaries-modified";

    public const string Critical = "critical";
    public const string Warning = "warning";

    /// <summary>What the dashboard says, and how worried to be. Unknown kinds (from a newer version)
    /// still show, as a warning with their raw name.</summary>
    public static (string Label, string Severity) Describe(string kind) => kind switch
    {
        LicenceOlder => ("Ancienne copie de la base remise : licence plus ancienne que la dernière reçue", Critical),
        LicenceMissing => ("Licence effacée de la base", Critical),
        LicenceInvalid => ("Licence modifiée à la main (signature invalide)", Critical),
        LicenceReplay => ("Ancienne licence renvoyée par un faux serveur", Critical),
        ClockBack => ("Horloge de l'ordinateur reculée", Warning),
        ServerUrlChanged => ("Adresse du serveur de licence effacée ou changée dans la base", Critical),
        ConfigTampered => ("Réglages de sécurité modifiés (appsettings.json)", Critical),
        ModeEdited => ("Mode de l'espace modifié dans la base", Critical),
        PhoneOnlySet => ("Espace marqué « téléphone uniquement » dans la base", Critical),
        UnknownDevice => ("Poste non enregistré refusé : nouveau poste, ou copie de l'installation ?", Warning),
        RevokedDevice => ("Ordinateur retiré qui essaie encore de renouveler la licence", Warning),
        UnboundDevice => ("Ordinateur inconnu qui essaie de renouveler la licence", Warning),
        HardwareChanged => ("Le matériel de ce poste a changé : disque ou carte mère remplacé, ou installation copiée sur un autre ordinateur", Warning),
        BinariesModified => ("Les fichiers du programme ont changé sans mise à jour de version : programme modifié (tentative de contournement)", Critical),
        _ => (kind, Warning),
    };
}
