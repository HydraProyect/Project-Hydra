using CaeManager.Application.Common;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Integraciones.Commands.ReactivarConexion;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Integraciones.Queries.ComprobarAccesoBuzon;

/// <summary>Lo que dice «Comprobar acceso» de un buzón ya dado de alta.</summary>
public enum ResultadoComprobacionAccesoBuzon
{
    /// <summary>Con el mismo token que lo abre, Microsoft devuelve la Bandeja de entrada del mismo buzón que se conectó.</summary>
    Ok,

    /// <summary>Microsoft responde 403 o 404 al abrir su Bandeja de entrada (<see cref="CodigosErrorMicrosoft365.EsSinAccesoBuzon"/>).</summary>
    SinAcceso,

    /// <summary>
    /// Otra conexión no desconectada del Tenant activo tiene la misma dirección o la misma identidad de buzón. Solo se mira el
    /// Tenant activo: si lo tiene conectado otro Tenant no se sabe aquí (la reclamación global solo se lee bajo RLS).
    /// </summary>
    Ocupado
}

/// <summary>
/// «Comprobar acceso» de una fila de Buzones: la misma comprobación que hace «Reconectar»
/// (<see cref="ReactivarConexionCommandHandler"/>), sin sus efectos. Pide el token con <see cref="AccesoGraphService"/> (el
/// de aplicación del directorio en un compartido, el delegado de la credencial en uno personal) y lee la identidad del buzón
/// con él, como el ciclo de verificación; no cambia el estado de la conexión, no la pasa a ConError, no toma el cerrojo de
/// verificación ni toca la suscripción ni la reclamación. Quien deja una conexión en ConError sigue siendo el ciclo diario o
/// «Reconectar».
/// <para>
/// No es un Command porque no muta ningún agregado, pero, como <c>ObtenerEstructuraBuzonQuery</c>, guarda al final en un
/// buzón personal: <see cref="AccesoGraphService"/> puede rotar el refresh token y no lo persiste por sí solo.
/// </para>
/// <para>
/// Solo el Administrador comprueba, con la misma guarda que «Reconectar»: el rol efectivo se mira antes de leer nada, y la
/// conexión tiene que ser visible para quien pregunta.
/// </para>
/// </summary>
[AreaDelEncargo("Integraciones")]
public record ComprobarAccesoBuzonQuery(Guid ConexionId) : IRequest<Result<ResultadoComprobacionAccesoBuzon>>;

public class ComprobarAccesoBuzonQueryHandler(
    IConexionIntegracionRepository conexionRepositorio,
    IIntegracionesQueryContext queryContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    AccesoGraphService accesoGraph,
    IMicrosoft365GraphClient graphClient,
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios)
    : IRequestHandler<ComprobarAccesoBuzonQuery, Result<ResultadoComprobacionAccesoBuzon>>
{
    public async Task<Result<ResultadoComprobacionAccesoBuzon>> Handle(
        ComprobarAccesoBuzonQuery request, CancellationToken cancellationToken)
    {
        if (await currentUserService.ObtenerRolEfectivoAsync() != "Administrador")
            return Fallo(
                DesconectarBuzonCommandHandler.CodigoSoloAdministrador,
                "Solo el Administrador puede comprobar el acceso a un buzón.");

        var conexion = await conexionRepositorio.ObtenerPorIdAsync(request.ConexionId, cancellationToken);
        if (conexion is null || conexion.Proveedor != ProveedorIntegracion.Microsoft365
            || !await alcanceDatos.ClienteOpcionalVisibleAsync(conexion.ClienteId, cancellationToken)
            || !await alcanceDatos.ConexionIntegracionVisibleAsync(conexion.Id, cancellationToken))
            return Fallo("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión.");

        // Mismo criterio que ConectarBuzonMicrosoft365CommandHandler para «ya tienes una conexión con este buzón»: dirección o
        // identidad, sin contar las desconectadas. Solo el Tenant activo (queryContext va bajo RLS).
        var buzonNormalizado = conexion.BuzonEmail.Trim().ToLowerInvariant();
        var identidad = conexion.IdentidadBuzon;
        if (await queryContext.ConexionesIntegracion.AnyAsync(
                c => c.Id != conexion.Id && c.Proveedor == ProveedorIntegracion.Microsoft365
                     && c.Estado != EstadoConexionIntegracion.Deshabilitada
                     && (c.BuzonEmail.ToLower() == buzonNormalizado || (identidad != null && c.IdentidadBuzon == identidad)),
                cancellationToken))
            return Result.Exito(ResultadoComprobacionAccesoBuzon.Ocupado);

        // Sin identidad guardada no hay con qué comparar lo que responda Microsoft: lo mismo que dice «Reconectar».
        if (identidad is null)
            return Fallo(
                ReactivarConexionCommandHandler.CodigoReconexionNecesaria,
                "Esta conexión no tiene la comprobación de buzón: desconéctala y vuelve a conectarla desde Conexiones.");

        var token = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);
        if (token.EsFallido)
            return SinComprobacion();

        var leida = await graphClient.ObtenerIdentidadBuzonAsync(token.Valor, BuzonGraph.De(conexion), cancellationToken);
        if (!conexion.EsBuzonCompartido)
            await GuardarTokenRotadoAsync(cancellationToken);

        if (leida.EsFallido)
            return CodigosErrorMicrosoft365.EsSinAccesoBuzon(leida.Error)
                ? Result.Exito(ResultadoComprobacionAccesoBuzon.SinAcceso)
                : SinComprobacion();

        return string.Equals(leida.Valor, identidad, StringComparison.Ordinal)
            ? Result.Exito(ResultadoComprobacionAccesoBuzon.Ok)
            : Fallo(
                ReactivarConexionCommandHandler.CodigoReconexionNecesaria,
                "La dirección de esta conexión corresponde ahora a otro buzón: desconéctala y vuelve a conectarla desde Conexiones.");
    }

    /// <summary>Un guardado fallido no deja la credencial rastreada para que la guarde otra operación del circuito.</summary>
    private async Task GuardarTokenRotadoAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            descarteCambios.DescartarCambiosPendientes();
            throw;
        }
    }

    private static Result<ResultadoComprobacionAccesoBuzon> SinComprobacion() => Fallo(
        ReactivarConexionCommandHandler.CodigoSinComprobacion, "No pudimos comprobar el buzón en Microsoft. Inténtalo de nuevo.");

    private static Result<ResultadoComprobacionAccesoBuzon> Fallo(string codigo, string mensaje) =>
        Result.Fallo<ResultadoComprobacionAccesoBuzon>(Error.Crear(codigo, mensaje));
}
