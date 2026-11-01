using System.Text;
using CaeManager.Domain.ApiKeys;

namespace CaeManager.Infrastructure.Autenticacion;

/// <summary>
/// Lectura única de las dos cabeceras de la Clave de Integración del Tenant. La comparten quien autentica y
/// el limitador, que reparte el cupo antes de autenticar: el mismo motivo que <see cref="CabeceraClaveApi"/>.
/// </summary>
public static class CabeceraClaveIntegracion
{
    private const string EsquemaToken = "Bearer ";
    private const string EsquemaCredenciales = "Basic ";

    /// <summary>
    /// El token de <c>Authorization: Bearer</c>, o <c>null</c> si la cabecera es de otro esquema. Vacío si
    /// el esquema es este y falta el token.
    /// </summary>
    public static string? ExtraerToken(string? cabeceraAuthorization) =>
        cabeceraAuthorization is not null && cabeceraAuthorization.StartsWith(EsquemaToken, StringComparison.OrdinalIgnoreCase)
            ? cabeceraAuthorization[EsquemaToken.Length..].Trim()
            : null;

    /// <summary>
    /// Las credenciales de cliente de <c>Authorization: Basic</c> (RFC 6749 § 2.3.1): el Id de la clave y
    /// su secreto. Solo en cabecera: el secreto no viaja en el cuerpo ni en la URL (ASVS V13.1.3). Cualquier
    /// otra forma devuelve <c>null</c>.
    /// </summary>
    public static CredencialesClaveIntegracion? ExtraerCredenciales(string? cabeceraAuthorization)
    {
        if (cabeceraAuthorization is null || !cabeceraAuthorization.StartsWith(EsquemaCredenciales, StringComparison.OrdinalIgnoreCase))
            return null;

        string texto;
        try
        {
            texto = Encoding.UTF8.GetString(Convert.FromBase64String(cabeceraAuthorization[EsquemaCredenciales.Length..].Trim()));
        }
        catch (FormatException)
        {
            return null;
        }

        var separador = texto.IndexOf(':');
        if (separador < 0 || !Guid.TryParse(texto[..separador], out var claveId))
            return null;

        var secreto = texto[(separador + 1)..];
        return secreto.StartsWith(ClaveIntegracionTenant.PrefijoSecreto, StringComparison.Ordinal)
            ? new CredencialesClaveIntegracion(claveId, secreto)
            : null;
    }
}

public sealed record CredencialesClaveIntegracion(Guid ClaveId, string Secreto)
{
    /// <summary>El secreto nunca sale por un <c>ToString</c> ni, con él, por un log.</summary>
    public override string ToString() => $"{nameof(CredencialesClaveIntegracion)} {{ ClaveId = {ClaveId} }}";
}
