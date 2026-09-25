using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.ContextoRls;

/// <summary>
/// <c>/salud</c> del contexto RLS firmado (P6): comprueba, con la identidad de
/// tráfico, que hay una clave vigente que este proceso sabe descifrar.
///
/// <para>
/// <b>Fase «expandir»: nunca Unhealthy.</b> Sin clave, o con menos de
/// <see cref="ClaveContextoRls.AvisoCaducidad"/> de vigencia, devuelve
/// Degraded (200): ninguna política lee todavía el contexto firmado, así que
/// el tráfico sigue sirviendo y marcar el contenedor como caído lo cortaría
/// sin motivo. Sin clave, además, se registra como error. En la fase
/// «contraer» la falta de clave deja las conexiones sin filas y este caso pasa
/// a Unhealthy.
/// </para>
/// </summary>
public sealed class ClaveContextoRlsHealthCheck(
    Func<string> cadenaDeTrafico,
    IDataProtectionProvider proteccion,
    TimeProvider reloj,
    ILogger<ClaveContextoRlsHealthCheck> log) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Conexión cruda de tráfico, sin interceptor: no lee filas de ningún
        // Tenant, solo llama a app_claves_contexto_protegidas().
        ClaveContextoLeida? clave;
        try
        {
            await using var conexion = new NpgsqlConnection(cadenaDeTrafico());
            await conexion.OpenAsync(cancellationToken);
            clave = await ClaveContextoRls.LeerVigenteAsync(conexion, proteccion, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin capturar, HealthCheckService lo convertiría en Unhealthy (503)
            // y cortaría el sondeo del despliegue. La caída de la base ya la
            // enseña el check de NpgSql; este solo avisa de la clave.
            log.LogError(ex, "No se pudo leer la clave del contexto RLS: las conexiones no llevan contexto firmado.");
            return HealthCheckResult.Degraded("No se pudo leer la clave del contexto RLS.", ex);
        }

        if (clave is null)
        {
            log.LogError(
                "No hay clave del contexto RLS que este proceso sepa descifrar: las conexiones no llevan " +
                "contexto firmado. Relanzar el migrador registra una nueva.");
            return HealthCheckResult.Degraded(
                "Sin clave del contexto RLS descifrable: el contexto no se firma.");
        }

        var restante = clave.ValidaHasta - reloj.GetUtcNow();
        if (restante <= ClaveContextoRls.AvisoCaducidad)
        {
            return HealthCheckResult.Degraded(
                $"La clave del contexto RLS caduca en {restante.TotalDays:0.0} días; relanzar el migrador la renueva.");
        }

        return HealthCheckResult.Healthy();
    }
}
