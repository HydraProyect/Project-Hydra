using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.DescartarSugerenciaVisita;

/// <summary>El Gestor decide que la sugerencia no aplica (falso positivo, o ya se gestionó a mano) — la retira de la Bandeja sin crear ninguna Visita.</summary>
public record DescartarSugerenciaVisitaCorreoCommand(Guid Id) : ICommand;

public class DescartarSugerenciaVisitaCorreoCommandHandler(
    ISugerenciaVisitaCorreoRepository repositorio, ICurrentUserService currentUserService, IUnitOfWork unitOfWork)
    : IRequestHandler<DescartarSugerenciaVisitaCorreoCommand, Result>
{
    public async Task<Result> Handle(DescartarSugerenciaVisitaCorreoCommand request, CancellationToken cancellationToken)
    {
        var sugerencia = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (sugerencia is null)
            return Result.Fallo(Error.Crear("SugerenciaVisitaCorreo.NoEncontrada", "No encontramos esta sugerencia."));

        sugerencia.Resolver(ResolucionSugerencia.Descartada, await currentUserService.ObtenerUsuarioActualIdAsync());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
