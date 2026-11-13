using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Email;

/// <summary>
/// Base pública utilizable en enlaces de cuenta: URL absoluta sin credenciales, consulta ni
/// fragmento. Fuera de Development debe ser https.
/// </summary>
public static class UrlBasePublica
{
    public static Uri? Normalizar(string? valor, bool exigirHttps)
    {
        if (Uri.TryCreate(valor, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (!exigirHttps && uri.Scheme == Uri.UriSchemeHttp))
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query)
            && string.IsNullOrEmpty(uri.Fragment))
            return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");

        return null;
    }

    /// <summary>
    /// Regla única de la base de todo enlace con token que sale por correo (activación,
    /// recuperación, «No he sido yo» y su contraseña nueva): la configurada en
    /// <c>Smtp:UrlBasePublica</c>. La base de la petición entrante (esquema, Host y
    /// PathBase, que controla quien la envía) solo se usa en Development y solo si no
    /// hay ninguna configurada; fuera de Development nunca, aunque la configurada falte
    /// o no sea válida (entonces <c>null</c>: sin enlace antes que con uno envenenado).
    /// OWASP ASVS 4.0.3 V2.5 (recuperación de credenciales) y V5.1.5 (destinos solo de
    /// una lista permitida).
    /// </summary>
    public static Uri? Resolver(string? configurada, Func<string?> basePeticion, bool desarrollo)
    {
        if (string.IsNullOrWhiteSpace(configurada) && desarrollo)
            configurada = basePeticion();

        return Normalizar(configurada, exigirHttps: !desarrollo);
    }
}

/// <summary>
/// Validación de arranque de <c>Smtp:UrlBasePublica</c>. Fuera de Development es
/// obligatoria y https: sin ella no hay enlaces de activación, de recuperación ni de
/// los avisos de seguridad («No he sido yo») seguros (el Host de la petición lo
/// controla quien la envía; ver <see cref="UrlBasePublica.Resolver"/>), así que la
/// aplicación no arranca. En Development puede faltar (se usa el Host local).
/// Mismo criterio que <c>ResolverCadenaDeTrafico</c>: un arranque que falla se ve en
/// el acto; un enlace de cuenta que no sale, no.
/// </summary>
public sealed class ValidadorUrlBasePublica(IHostEnvironment entorno) : IValidateOptions<SmtpEmailOptions>
{
    public ValidateOptionsResult Validate(string? name, SmtpEmailOptions options)
    {
        var desarrollo = entorno.IsDevelopment();
        if (desarrollo && string.IsNullOrWhiteSpace(options.UrlBasePublica))
            return ValidateOptionsResult.Success;

        return UrlBasePublica.Normalizar(options.UrlBasePublica, exigirHttps: !desarrollo) is not null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"Smtp:UrlBasePublica (variable Smtp__UrlBasePublica) es obligatoria en el entorno '{entorno.EnvironmentName}' " +
                "y debe ser una URL absoluta https, sin credenciales, consulta ni fragmento (p. ej. https://app.talveg.es). " +
                "Sin ella los enlaces de activación y recuperación de contraseña no pueden construirse de forma segura.");
    }
}
