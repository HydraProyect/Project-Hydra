using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Importacion;
using MediatR;

namespace CaeManager.Application.Importacion.Commands.RegistrarHistorialImportacion;

/// <summary>
/// Escribe la fila de historial de una importación ya ejecutada (éxito o
/// fallo) — nunca se llama tras un simple análisis, solo tras intentar
/// confirmar de verdad. Comando separado de EjecutarImportacion(Combinada)
/// a propósito: esos dos comandos ya existen, están probados y no conocen
/// el concepto de "plantilla" (nombre libre del wizard) ni necesitan
/// conocerlo — la Web dispara este comando aparte justo después de recibir
/// su resultado, sin tocar su lógica de escritura.
/// </summary>
public record RegistrarHistorialImportacionCommand(
    string Plantilla, string NombreArchivo, bool Exitosa,
    int TotalCreados, int TotalAdvertencias, int TotalOmitidos, string? MensajeError) : ICommand;

public class RegistrarHistorialImportacionCommandHandler(
    IHistorialImportacionRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService usuarioActual)
    : IRequestHandler<RegistrarHistorialImportacionCommand, Result>
{
    // Application no puede referenciar Infrastructure.Identity.Roles — mismo motivo que en AutorizacionEscrituraBehavior.
    // Aunque este comando "solo" escribe una fila de historial, es alcanzable por MediatR con cualquier rol de
    // escritura: sin este guard, un rol no-Administrador podría inyectar un registro de historial fabricado
    // (éxito, totales) sin haber ejecutado ninguna importación real — corrompe el rastro de auditoría del propio
    // flujo que este comando existe para dejar por escrito.
    private const string RolAdministrador = "Administrador";

    public async Task<Result> Handle(RegistrarHistorialImportacionCommand request, CancellationToken cancellationToken)
    {
        if (await usuarioActual.ObtenerRolActualAsync() != RolAdministrador)
            return Result.Fallo(Error.Crear(
                "Importacion.SoloAdministrador", "Solo Administrador puede registrar el historial de una importación."));

        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync() ?? Guid.Empty;

        var registro = request.Exitosa
            ? HistorialImportacion.Exito(
                request.Plantilla, request.NombreArchivo, usuarioId,
                request.TotalCreados, request.TotalAdvertencias, request.TotalOmitidos)
            : HistorialImportacion.Fallo(
                request.Plantilla, request.NombreArchivo, usuarioId,
                request.MensajeError ?? "Fallo desconocido.");

        repositorio.Agregar(registro);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
