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
}

/// <summary>
/// Validación de arranque de <c>Smtp:UrlBasePublica</c>. Fuera de Development es
/// obligatoria y https: sin ella no hay enlaces de activación ni de recuperación
/// seguros (el Host de la petición lo controla quien la envía), así que la
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
