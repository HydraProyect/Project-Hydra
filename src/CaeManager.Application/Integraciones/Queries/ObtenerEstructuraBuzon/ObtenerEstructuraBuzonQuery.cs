using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using MediatR;

namespace CaeManager.Application.Integraciones.Queries.ObtenerEstructuraBuzon;

/// <summary>
/// Consulta en vivo contra Graph (no contra datos ya ingeridos) — la
/// petición explícita del usuario de "consultar la estructura de carpetas
/// que tenga el buzón" no se resuelve con lo que ya llegó por webhook
/// (Inbox únicamente, desde que se conectó el buzón): pide navegar el
/// buzón real completo. No es un Command a propósito (no muta ningún
/// agregado de dominio), pero sí llama a <see cref="IUnitOfWork.SaveChangesAsync"/>
/// al final — <see cref="AccesoGraphService"/> puede rotar el refresh token
/// guardado sin persistirlo por sí solo (ver su propio comentario); sin este
/// guardado, cada consulta de solo lectura dejaría la credencial cada vez
/// más desactualizada hasta que Graph rechazara el refresh token viejo.
/// </summary>
public record ObtenerEstructuraBuzonQuery(Guid ConexionIntegracionId) : IRequest<Result<IReadOnlyList<CarpetaGraphDto>>>;

public class ObtenerEstructuraBuzonQueryHandler(
    IConexionIntegracionRepository conexionRepositorio, AccesoGraphService accesoGraph, IMicrosoft365GraphClient graphClient,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<ObtenerEstructuraBuzonQuery, Result<IReadOnlyList<CarpetaGraphDto>>>
{
    public async Task<Result<IReadOnlyList<CarpetaGraphDto>>> Handle(
        ObtenerEstructuraBuzonQuery request, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(request.ConexionIntegracionId, cancellationToken);
        if (conexion is null || !await alcanceDatos.ClienteOpcionalVisibleAsync(conexion.ClienteId, cancellationToken))
            return Result.Fallo<IReadOnlyList<CarpetaGraphDto>>(
                Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        if (conexion.Estado != EstadoConexionIntegracion.Habilitada)
            return Result.Fallo<IReadOnlyList<CarpetaGraphDto>>(
                Error.Crear("ConexionIntegracion.NoDisponible", "Este buzón no está disponible."));

        var tokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion.Id, cancellationToken);
        if (tokenResultado.EsFallido)
            return Result.Fallo<IReadOnlyList<CarpetaGraphDto>>(tokenResultado.Error);

        var carpetasResultado = await graphClient.ListarCarpetasAsync(tokenResultado.Valor, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return carpetasResultado;
    }
}
