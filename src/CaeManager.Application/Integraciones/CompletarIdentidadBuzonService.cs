using CaeManager.Application.Common;
using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Comprueba en cada ciclo de renovación la identidad estable (<see cref="ConexionIntegracion.IdentidadBuzon"/>)
/// de las conexiones de Microsoft 365 del Tenant actual que no están Deshabilitadas, con el mismo token que las
/// abre. Lo invoca la renovación de suscripciones, que ya recorre cada Tenant, antes de renovar, y solo
/// rehabilita las conexiones que este servicio devuelve como verificadas en ese mismo ciclo.
/// <list type="bullet">
/// <item>Sin identidad (dada de alta antes de que existiera): se completa en la conexión y en su
/// <see cref="ReclamacionBuzonIntegracion"/>. Si choca con la de otra reclamación, el mismo buzón está conectado
/// en otro Tenant con otra dirección: esta conexión pasa a ConError (la ingesta descarta lo que llegue a una
/// conexión que no está Habilitada), su identidad queda vacía y ni la renovación ni Reactivar la levantan: hay
/// que volver a conectarla, y ahí decide el índice único. La del otro Tenant no se toca: queda fuera del
/// alcance de esta sesión por RLS.</item>
/// <item>Con identidad distinta de la que responde hoy a su dirección (el directorio reasignó la dirección a
/// otro buzón): pasa a ConError y tampoco se levanta sola.</item>
/// </list>
/// Un token o una lectura que fallan no marcan nada aquí: la conexión no sale verificada, la renovación no la
/// rehabilita y se reintenta en el ciclo siguiente.
/// </summary>
public class CompletarIdentidadBuzonService(
    IIntegracionesQueryContext queryContext,
    IConexionIntegracionRepository conexionRepositorio,
    IReclamacionBuzonIntegracionRepository reclamacionRepositorio,
    AccesoGraphService accesoGraph,
    IMicrosoft365GraphClient graphClient,
    IUnitOfWork unitOfWork,
    ILogger<CompletarIdentidadBuzonService> logger)
{
    public const string MensajeBuzonDuplicado =
        "Otra organización tiene conectado este mismo buzón con otra dirección. Desconéctalo en una de las dos.";

    public const string MensajeBuzonCambiado =
        "La dirección de esta conexión corresponde ahora a otro buzón. Vuelve a conectarla desde Conexiones.";

    /// <returns>Las conexiones cuya identidad se leyó y coincide (o se acaba de completar) en este ciclo.</returns>
    public async Task<IReadOnlySet<Guid>> CompletarAsync(CancellationToken cancellationToken)
    {
        var verificadas = new HashSet<Guid>();
        var activas = await queryContext.ConexionesIntegracion
            .Where(c => c.Proveedor == ProveedorIntegracion.Microsoft365 && c.Estado != EstadoConexionIntegracion.Deshabilitada)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        foreach (var conexionId in activas)
        {
            var conexion = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
            if (conexion is null)
                continue;

            var token = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);
            if (token.EsFallido)
                continue;

            var identidad = await graphClient.ObtenerIdentidadBuzonAsync(token.Valor, BuzonGraph.De(conexion), cancellationToken);
            if (identidad.EsFallido)
            {
                logger.LogWarning(
                    "No se pudo leer la identidad del buzón de la conexión {ConexionId}: {Error}", conexionId, identidad.Error.Mensaje);
                continue;
            }

            if (conexion.IdentidadBuzon is { } guardada)
            {
                if (string.Equals(guardada, identidad.Valor, StringComparison.Ordinal))
                {
                    verificadas.Add(conexionId);
                    // Guarda el token refrescado (si lo hubo), para no volver a rotarlo en la renovación.
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    continue;
                }

                logger.LogError(
                    "La dirección de la conexión {ConexionId} responde ahora con otro buzón: pasa a ConError.", conexionId);
                conexion.MarcarConError(MensajeBuzonCambiado);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                continue;
            }

            conexion.FijarIdentidadBuzon(identidad.Valor);
            var reclamacion = await reclamacionRepositorio.ObtenerPorConexionAsync(conexionId, cancellationToken);
            reclamacion?.FijarIdentidadBuzon(identidad.Valor);

            if (await reclamacionRepositorio.GuardarCambiosSiBuzonLibreAsync(cancellationToken))
            {
                verificadas.Add(conexionId);
                continue;
            }

            // El guardado fallido descartó los cambios en memoria: se vuelve a leer la conexión para marcarla.
            logger.LogError(
                "La conexión {ConexionId} tiene el mismo buzón que una reclamación de otro Tenant con otra dirección: pasa a ConError.",
                conexionId);
            var duplicada = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
            if (duplicada is null)
                continue;

            duplicada.MarcarConError(MensajeBuzonDuplicado);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        return verificadas;
    }
}
