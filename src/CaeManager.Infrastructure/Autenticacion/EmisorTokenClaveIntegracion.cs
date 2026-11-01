using CaeManager.Application.Common;
using CaeManager.Domain.ApiKeys;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Autenticacion;

/// <summary>
/// Canjea el secreto de una Clave de Integración del Tenant por un token (D-3). Igual que al autenticar la
/// Clave de Soporte TALVEG: primero la función acotada dice de quién es el secreto y solo entonces se entra
/// en ese Tenant y se lee la fila bajo RLS. Aquí se registra el último uso, y no en cada petición: el token
/// vive 15 minutos, así que el dato conserva su utilidad sin una escritura por lectura.
/// </summary>
public class EmisorTokenClaveIntegracion(
    IClaveIntegracionTenantRepository claves,
    IUnitOfWork unitOfWork,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<EmisorTokenClaveIntegracion> logger)
{
    /// <summary>El token, o <c>null</c> sea cual sea el motivo: quien llama no aprende por qué.</summary>
    public async Task<string?> EmitirAsync(CredencialesClaveIntegracion credenciales, CancellationToken cancellationToken = default)
    {
        var hash = CabeceraClaveApi.HashDeClave(credenciales.Secreto);
        var tenantId = await claves.ObtenerTenantPorHashAsync(hash, cancellationToken);
        if (tenantId is null)
            return null;

        using var ambitoTenant = AmbitoTenantExplicito.Establecer(tenantId.Value);

        var clave = await claves.ObtenerPorHashAsync(hash, cancellationToken);
        if (clave is null || clave.Id != credenciales.ClaveId || !clave.PuedeAutenticar(DateTime.UtcNow)
            || !await claves.TenantAdmiteClaveAsync(tenantId.Value, clave.CreadoEnUtc, cancellationToken))
            return null;

        await RegistrarUsoAsync(clave, cancellationToken);

        return TokenClaveIntegracion.Proteger(dataProtectionProvider, clave.Id, tenantId.Value);
    }

    /// <summary>Best-effort, como en <see cref="ApiKeyAuthenticationHandler"/>: perder el dato no tumba la integración.</summary>
    private async Task RegistrarUsoAsync(ClaveIntegracionTenant clave, CancellationToken cancellationToken)
    {
        try
        {
            using var ambitoActor = AmbitoActorAuditoria.EstablecerIntegracionExterna();
            clave.RegistrarUso();
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo registrar el último uso de la Clave de Integración del Tenant {ClaveId}.", clave.Id);
        }
    }
}
