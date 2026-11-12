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

        // Un buzón personal de OTRO gestor no se resuelve por cartera de
        // Cliente (tiene ClienteId null, igual que el genérico del tenant) —
        // mismo hallazgo y mismo fix que EnviarMensajeNuevoCommandHandler.
        if (!await alcanceDatos.ConexionIntegracionVisibleAsync(conexion.Id, cancellationToken))
            return Result.Fallo<IReadOnlyList<CarpetaGraphDto>>(
                Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        if (conexion.Estado != EstadoConexionIntegracion.Habilitada)
            return Result.Fallo<IReadOnlyList<CarpetaGraphDto>>(
                Error.Crear("ConexionIntegracion.NoDisponible", "Este buzón no está disponible."));

        // Sin identidad verificada en la ventana, la dirección puede corresponder hoy a otro buzón: no se muestra.
        if (!conexion.OperativaVerificada(DateTime.UtcNow))
            return Result.Fallo<IReadOnlyList<CarpetaGraphDto>>(ErroresBuzonSinVerificar.Crear());

        var tokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion, cancellationToken);
        if (tokenResultado.EsFallido)
            return Result.Fallo<IReadOnlyList<CarpetaGraphDto>>(tokenResultado.Error);

        // Pedir el token puede tardar (y rotar el refresh token): se relee la conexión de la base justo antes de llamar a
        // Graph, y otra vez antes de cada petición de carpetas hijas. Si otra operación la desconectó o la pasó a ConError
        // entre medias, no se muestra el buzón. El token rotado se guarda igual.
        if (!await conexionRepositorio.SigueOperativaVerificadaAsync(conexion.Id, DateTime.UtcNow, cancellationToken))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Fallo<IReadOnlyList<CarpetaGraphDto>>(ErroresBuzonSinVerificar.Crear());
        }

        var carpetasResultado = await graphClient.ListarCarpetasAsync(
            tokenResultado.Valor, BuzonGraph.De(conexion),
            ct => conexionRepositorio.SigueOperativaVerificadaAsync(conexion.Id, DateTime.UtcNow, ct), cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return carpetasResultado;
    }
}
