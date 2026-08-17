using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Reportes;
using MediatR;

namespace CaeManager.Application.Reportes.Commands.RegistrarHistorialInforme;

/// <summary>Escribe la fila de historial al generar la vista previa de un informe — no al descargarlo, igual que el propio panel "Historial" del mockup nace con la generación.</summary>
public record RegistrarHistorialInformeCommand(string TipoInforme, Guid? ClienteId, string? ClienteNombre) : ICommand;

public class RegistrarHistorialInformeCommandHandler(
    IHistorialInformeRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService usuarioActual)
    : IRequestHandler<RegistrarHistorialInformeCommand, Result>
{
    public async Task<Result> Handle(RegistrarHistorialInformeCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync() ?? Guid.Empty;

        repositorio.Agregar(new HistorialInforme(request.TipoInforme, request.ClienteId, request.ClienteNombre, usuarioId));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
