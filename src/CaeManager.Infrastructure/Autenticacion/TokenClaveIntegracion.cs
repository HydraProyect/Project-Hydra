using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Infrastructure.Autenticacion;

/// <summary>
/// Token de acceso de una Clave de Integración del Tenant (D-3): opaco, de 15 minutos y sin refresco.
/// Data Protection y no un JWT por lo mismo que <see cref="TokenSesionExtension"/>: nadie más lo consume.
/// El token solo dice qué clave es y de qué Tenant; estado y alcances se leen de la fila en cada petición,
/// así que revocar corta en la petición siguiente y no a los 15 minutos.
/// </summary>
public static class TokenClaveIntegracion
{
    /// <summary><b>No renombrar</b>: invalidaría de golpe todos los tokens vivos.</summary>
    private const string PropositoProtector = "CaeManager.ClaveIntegracion.Token.v1";

    public static readonly TimeSpan Vigencia = TimeSpan.FromMinutes(15);

    public static string Proteger(IDataProtectionProvider dataProtectionProvider, Guid claveId, Guid tenantId) =>
        CrearProtector(dataProtectionProvider).Protect($"{claveId:N}|{tenantId:N}", Vigencia);

    /// <summary>
    /// La carga útil, o <c>null</c> si el token está caducado, manipulado o mal formado: quien llama no
    /// puede distinguirlos y ninguno concede acceso.
    /// </summary>
    public static CargaUtilTokenClaveIntegracion? Leer(IDataProtectionProvider dataProtectionProvider, string token)
    {
        string desprotegido;
        try
        {
            desprotegido = CrearProtector(dataProtectionProvider).Unprotect(token);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }

        var partes = desprotegido.Split('|');
        return partes.Length == 2
            && Guid.TryParseExact(partes[0], "N", out var claveId) && claveId != Guid.Empty
            && Guid.TryParseExact(partes[1], "N", out var tenantId) && tenantId != Guid.Empty
                ? new CargaUtilTokenClaveIntegracion(claveId, tenantId)
                : null;
    }

    private static ITimeLimitedDataProtector CrearProtector(IDataProtectionProvider dataProtectionProvider) =>
        dataProtectionProvider.CreateProtector(PropositoProtector).ToTimeLimitedDataProtector();
}

public record CargaUtilTokenClaveIntegracion(Guid ClaveId, Guid TenantId);
