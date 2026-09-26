using CaeManager.Application.Common;
using CaeManager.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CaeManager.Infrastructure.Auditing;

/// <summary>
/// Mantiene creadas las particiones mensuales futuras de los registros de
/// auditoría (P1-M2): una vez al día llama a
/// <c>app_asegurar_particiones_eventos</c>, la única función del particionado
/// que <c>cae_app_runtime</c> puede ejecutar. La función es SECURITY DEFINER,
/// no recibe nombres ni SQL y recorre una lista fija de tablas; runtime sigue
/// sin poder crear, modificar ni borrar tablas por sí mismo.
///
/// <para>
/// La migración deja creados los meses hasta el actual más
/// <see cref="MesesPorDelante"/>, así que si este servicio deja de correr hay
/// ese margen; después, los eventos caen en la partición por defecto y no se
/// pierden. Que haya eventos en ella es la señal de que algo no funciona, y se
/// registra como error.
/// </para>
///
/// <para>
/// <b>Con la identidad del tráfico y sin elección de líder.</b> La conexión es la
/// de <see cref="InfrastructureServiceCollectionExtensions.ResolverCadenaDeTrafico"/>,
/// la misma que el DbContext inyectado: en staging y producción el contenedor
/// <c>app</c> recibe <c>CaeManagerDb</c> vacía (P0-2) y solo tiene
/// <c>CaeManagerDbRuntime</c> (hallazgo de Codex). No usa
/// <c>IEleccionLiderService</c>, que lee <c>CaeManagerDb</c>: la función ya se
/// serializa con <c>pg_advisory_xact_lock</c> y es idempotente, así que varias
/// réplicas a la vez solo repiten una comprobación barata.
/// </para>
///
/// <para>
/// Sin ámbito de Tenant: la función no lee ni escribe datos de ningún Tenant
/// salvo para mover a su mes las filas de la partición por defecto, y eso lo
/// hace como propietario, dentro de la propia función.
/// </para>
/// </summary>
public class ParticionesEventosHostedService(
    IConfiguration configuration,
    IHostEnvironment entorno,
    ILogger<ParticionesEventosHostedService> logger)
    : BackgroundService
{
    /// <summary>Igual que <c>ParticionadoMensualEventos.MesesPorDelante</c> del proyecto de migraciones.</summary>
    public const int MesesPorDelante = 3;

    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // P41c: lo que haga este servicio lo hace la plataforma. Hoy la función
        // no genera auditoría, pero la declaración va en el punto de entrada
        // para que ningún camino añadido después se quede fuera.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        using var temporizador = new PeriodicTimer(IntervaloSondeo);

        await EjecutarCicloAsync(stoppingToken);

        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await EjecutarCicloAsync(stoppingToken);
    }

    private async Task EjecutarCicloAsync(CancellationToken stoppingToken)
    {
        try
        {
            _ = await AsegurarAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló la creación de las particiones mensuales futuras de la auditoría.");
        }
    }

    public async Task<ResultadoParticiones> AsegurarAsync(CancellationToken cancellationToken)
    {
        var cadenaConexion = InfrastructureServiceCollectionExtensions.ResolverCadenaDeTrafico(configuration, entorno);

        await using var conexion = new NpgsqlConnection(cadenaConexion);
        await conexion.OpenAsync(cancellationToken);

        await using var orden = new NpgsqlCommand(
            "SELECT particiones_creadas, eventos_en_defecto FROM app_asegurar_particiones_eventos(@meses);", conexion);
        orden.Parameters.AddWithValue("meses", MesesPorDelante);

        await using var lector = await orden.ExecuteReaderAsync(cancellationToken);
        await lector.ReadAsync(cancellationToken);
        var resultado = new ResultadoParticiones(lector.GetInt32(0), lector.GetInt64(1));

        if (resultado.ParticionesCreadas > 0)
            logger.LogInformation("Creadas {Particiones} particiones mensuales de auditoría.", resultado.ParticionesCreadas);

        if (resultado.EventosEnDefecto > 0)
            logger.LogError(
                "Hay {Eventos} eventos de auditoría en la partición por defecto: su mes no tenía partición al escribirse.",
                resultado.EventosEnDefecto);

        return resultado;
    }

    public sealed record ResultadoParticiones(int ParticionesCreadas, long EventosEnDefecto);
}
