using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence;

/// <summary>
/// Comprueba al arrancar si el tráfico normal está usando de verdad el rol
/// restringido de <c>RUNBOOK-RLS.md</c> (auditoría Módulo 8, hallazgo
/// crítico: <c>ConnectionStrings:CaeManagerDbRuntime</c> es opcional y hoy no
/// está configurado en ningún entorno — ver
/// <c>InfrastructureServiceCollectionExtensions</c> y
/// <c>TenantRlsConnectionInterceptor</c>). Mientras la conexión de tráfico
/// siga siendo la del propietario de las tablas, <c>FORCE ROW LEVEL
/// SECURITY</c> no aporta nada: el propietario no está sujeto a RLS y
/// conserva capacidad para alterar o desactivar las políticas.
///
/// Mismo criterio "ruidoso a propósito" que
/// <see cref="CaeManager.Infrastructure.DataProtection.VerificacionKmsHostedService"/>:
/// no tumba el arranque. Forzarlo hoy rompería el único entorno que existe —
/// el rol restringido todavía no está aprovisionado en ningún sitio (ver ese
/// mismo runbook) — y un despliegue que crea tener aislamiento Zero-Trust y
/// en realidad no lo tenga es peor que uno que lo sepa. Lo que sí cambia es
/// que ahora queda dicho en cada arranque, no solo en un comentario que nadie
/// vuelve a mirar.
/// </summary>
public class VerificacionRolRuntimeHostedService(
    IConfiguration configuration, ILogger<VerificacionRolRuntimeHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var cadenaRuntime = configuration.GetConnectionString("CaeManagerDbRuntime");

        if (string.IsNullOrWhiteSpace(cadenaRuntime))
        {
            logger.LogWarning(
                "[AVISO] ConnectionStrings:CaeManagerDbRuntime no está configurado — el tráfico normal " +
                "sigue conectando como el rol propietario de las tablas (CaeManagerDb). FORCE ROW LEVEL " +
                "SECURITY no restringe al propietario ni a un superusuario: no hay aislamiento Zero-Trust " +
                "real todavía, solo el filtro global de EF Core. Ver RUNBOOK-RLS.md para aprovisionar el " +
                "rol restringido.");
            return;
        }

        try
        {
            await using var conexion = new NpgsqlConnection(cadenaRuntime);
            await conexion.OpenAsync(cancellationToken);

            await using var comando = conexion.CreateCommand();
            comando.CommandText =
                "SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user;";
            await using var lector = await comando.ExecuteReaderAsync(cancellationToken);

            if (!await lector.ReadAsync(cancellationToken))
            {
                logger.LogError(
                    "CaeManagerDbRuntime: no se pudo resolver el rol de conexión ({Rol}) contra pg_roles.",
                    conexion.UserName);
                return;
            }

            var esSuperusuario = lector.GetBoolean(0);
            var puedeSaltarseRls = lector.GetBoolean(1);

            if (esSuperusuario || puedeSaltarseRls)
            {
                logger.LogError(
                    "CaeManagerDbRuntime está configurado pero el rol {Rol} tiene rolsuper={EsSuperusuario} " +
                    "rolbypassrls={PuedeSaltarseRls} — con cualquiera de los dos en true, RLS no restringe " +
                    "nada para este rol pase lo que pase FORCE ROW LEVEL SECURITY. Revisa RUNBOOK-RLS.md.",
                    conexion.UserName, esSuperusuario, puedeSaltarseRls);
                return;
            }

            logger.LogInformation(
                "CaeManagerDbRuntime operativo: el tráfico normal usa el rol restringido {Rol} " +
                "(rolsuper=false, rolbypassrls=false) — las políticas RLS de HabilitarRlsPostgres " +
                "restringen de verdad, incluso frente a un SQL administrativo que se saltara el filtro " +
                "global de EF Core.", conexion.UserName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "CaeManagerDbRuntime está configurado pero la conexión de verificación falló al arrancar. " +
                "No se pudo confirmar si el rol restringido está operativo.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
