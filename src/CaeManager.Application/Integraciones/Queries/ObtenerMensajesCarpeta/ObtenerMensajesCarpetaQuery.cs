using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using MediatR;

namespace CaeManager.Application.Integraciones.Queries.ObtenerMensajesCarpeta;

/// <summary>Historial de una carpeta del buzón real — ver <c>ObtenerEstructuraBuzonQuery</c> sobre por qué esto llama a Graph en vivo y por qué guarda cambios pese a ser una consulta.</summary>
public record ObtenerMensajesCarpetaQuery(Guid ConexionIntegracionId, string CarpetaExternoId, int Pagina = 1)
    : IRequest<Result<IReadOnlyList<MensajeResumenGraphDto>>>;

public class ObtenerMensajesCarpetaQueryHandler(
    IConexionIntegracionRepository conexionRepositorio, AccesoGraphService accesoGraph, IMicrosoft365GraphClient graphClient,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<ObtenerMensajesCarpetaQuery, Result<IReadOnlyList<MensajeResumenGraphDto>>>
{
    private const int TamanoPagina = 25;

    public async Task<Result<IReadOnlyList<MensajeResumenGraphDto>>> Handle(
        ObtenerMensajesCarpetaQuery request, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(request.ConexionIntegracionId, cancellationToken);
        if (conexion is null || !await alcanceDatos.ClienteOpcionalVisibleAsync(conexion.ClienteId, cancellationToken))
            return Result.Fallo<IReadOnlyList<MensajeResumenGraphDto>>(
                Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        if (conexion.Estado != EstadoConexionIntegracion.Habilitada)
            return Result.Fallo<IReadOnlyList<MensajeResumenGraphDto>>(
                Error.Crear("ConexionIntegracion.NoDisponible", "Este buzón no está disponible."));

        var tokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion.Id, cancellationToken);
        if (tokenResultado.EsFallido)
            return Result.Fallo<IReadOnlyList<MensajeResumenGraphDto>>(tokenResultado.Error);

        var skip = (Math.Max(1, request.Pagina) - 1) * TamanoPagina;
        var mensajesResultado = await graphClient.ListarMensajesAsync(
            tokenResultado.Valor, request.CarpetaExternoId, TamanoPagina, skip, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return mensajesResultado;
    }
}
