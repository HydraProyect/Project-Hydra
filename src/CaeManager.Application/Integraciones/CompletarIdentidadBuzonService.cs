using CaeManager.Application.Common;
using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Completa la identidad estable (<see cref="ConexionIntegracion.IdentidadBuzon"/>) de las conexiones de Microsoft
/// 365 del Tenant actual dadas de alta antes de que existiera, y la de su <see cref="ReclamacionBuzonIntegracion"/>.
/// Sin esto, una reclamación antigua solo guardaría la dirección, y un alias del mismo buzón (UPN, proxy) podría
/// darse de alta en otro Tenant. Lo invoca la renovación de suscripciones, que ya recorre cada Tenant con sus
/// tokens, antes de renovar.
///
/// Si la identidad choca con la de otra reclamación, el mismo buzón está conectado en otro Tenant con otra
/// dirección: esta conexión pasa a ConError (la ingesta descarta lo que llegue a una conexión que no está
/// Habilitada) y se registra. La del otro Tenant no se toca: queda fuera del alcance de esta sesión por RLS.
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

    public async Task CompletarAsync(CancellationToken cancellationToken)
    {
        var pendientes = await queryContext.ConexionesIntegracion
            .Where(c => c.Proveedor == ProveedorIntegracion.Microsoft365 && c.IdentidadBuzon == null &&
                        c.Estado != EstadoConexionIntegracion.Deshabilitada)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        foreach (var conexionId in pendientes)
        {
            var conexion = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
            if (conexion is null)
                continue;

            // Un token o una lectura que fallan no se marcan aquí: la renovación de esa misma conexión, justo
            // después, los encuentra y deja la conexión ConError con su motivo. Se reintenta en el ciclo siguiente.
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

            conexion.FijarIdentidadBuzon(identidad.Valor);
            var reclamacion = await reclamacionRepositorio.ObtenerPorConexionAsync(conexionId, cancellationToken);
            reclamacion?.FijarIdentidadBuzon(identidad.Valor);

            if (await reclamacionRepositorio.GuardarCambiosSiBuzonLibreAsync(cancellationToken))
                continue;

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
    }
}
