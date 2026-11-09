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
/// Una lectura de la identidad que falla deja la conexión en ConError (falla cerrado: sin saber qué buzón responde
/// a la dirección, no se ingiere); un ciclo posterior que la compruebe permite a la renovación rehabilitarla. Un
/// token que falla no marca nada (sin token tampoco se lee ningún mensaje). En ninguno de los dos casos la conexión
/// sale verificada.
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
        "La dirección de esta conexión corresponde ahora a otro buzón. Desconéctala y vuelve a conectarla desde Conexiones.";

    public const string MensajeSinComprobar =
        "No pudimos comprobar qué buzón responde a esta dirección. Se reintentará en la próxima renovación, o puedes reactivarla desde Conexiones.";

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
            // Una conexión que lanza no deja sin comprobar a las siguientes del Tenant: no sale verificada (la
            // renovación no la rehabilita) y se reintenta en el ciclo siguiente.
            try
            {
                if (await ComprobarAsync(conexionId, cancellationToken))
                    verificadas.Add(conexionId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "No se pudo comprobar la identidad del buzón de la conexión {ConexionId}.", conexionId);
            }
        }

        return verificadas;
    }

    /// <returns>true si la identidad se leyó y coincide con la guardada, o se acaba de completar.</returns>
    private async Task<bool> ComprobarAsync(Guid conexionId, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
        if (conexion is null)
            return false;

        // Sin token tampoco se puede leer ningún mensaje del buzón: no hay nada que cerrar aquí.
        var token = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);
        if (token.EsFallido)
        {
            logger.LogWarning(
                "No se pudo obtener el token de la conexión {ConexionId} para comprobar su buzón: {Error}", conexionId, token.Error.Mensaje);
            return false;
        }

        var identidad = await graphClient.ObtenerIdentidadBuzonAsync(token.Valor, BuzonGraph.De(conexion), cancellationToken);
        if (identidad.EsFallido)
        {
            // Falla cerrado: sin comprobar qué buzón responde a la dirección, la conexión deja de ingerir hasta que
            // un ciclo posterior la compruebe (y la renovación la rehabilite) o la reactive el Administrador. Si ya
            // estaba en ConError se deja su motivo, que es más concreto.
            logger.LogWarning(
                "No se pudo leer la identidad del buzón de la conexión {ConexionId}: {Error}", conexionId, identidad.Error.Mensaje);
            if (conexion.Estado == EstadoConexionIntegracion.Habilitada)
                conexion.MarcarConError(MensajeSinComprobar);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return false;
        }

        if (conexion.IdentidadBuzon is { } guardada)
        {
            if (string.Equals(guardada, identidad.Valor, StringComparison.Ordinal))
            {
                // Guarda el token refrescado (si lo hubo), para no volver a rotarlo en la renovación.
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return true;
            }

            logger.LogError(
                "La dirección de la conexión {ConexionId} responde ahora con otro buzón: pasa a ConError.", conexionId);
            conexion.MarcarConError(MensajeBuzonCambiado);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return false;
        }

        conexion.FijarIdentidadBuzon(identidad.Valor);
        var reclamacion = await reclamacionRepositorio.ObtenerPorConexionAsync(conexionId, cancellationToken);
        reclamacion?.FijarIdentidadBuzon(identidad.Valor);

        if (await reclamacionRepositorio.GuardarCambiosSiBuzonLibreAsync(cancellationToken))
            return true;

        // El guardado fallido descartó los cambios en memoria: se vuelve a leer la conexión para marcarla.
        logger.LogError(
            "La conexión {ConexionId} tiene el mismo buzón que una reclamación de otro Tenant con otra dirección: pasa a ConError.",
            conexionId);
        var duplicada = await conexionRepositorio.ObtenerPorIdAsync(conexionId, cancellationToken);
        if (duplicada is null)
            return false;

        duplicada.MarcarConError(MensajeBuzonDuplicado);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return false;
    }
}
