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
    public async Task<Result> Handle(RegistrarHistorialImportacionCommand request, CancellationToken cancellationToken)
    {
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
