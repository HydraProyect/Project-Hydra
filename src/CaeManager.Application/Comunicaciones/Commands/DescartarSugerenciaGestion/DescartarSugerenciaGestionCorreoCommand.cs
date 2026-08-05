using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.DescartarSugerenciaGestion;

/// <summary>El Gestor decide que la sugerencia no aplica (falso positivo, o ya se gestionó a mano) — la retira de la Bandeja sin crear ninguna Gestion.</summary>
public record DescartarSugerenciaGestionCorreoCommand(Guid Id) : ICommand;

public class DescartarSugerenciaGestionCorreoCommandHandler(ISugerenciaGestionCorreoRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<DescartarSugerenciaGestionCorreoCommand, Result>
{
    public async Task<Result> Handle(DescartarSugerenciaGestionCorreoCommand request, CancellationToken cancellationToken)
    {
        var sugerencia = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (sugerencia is null)
            return Result.Fallo(Error.Crear("SugerenciaGestionCorreo.NoEncontrada", "No encontramos esta sugerencia."));

        sugerencia.Resolver();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
