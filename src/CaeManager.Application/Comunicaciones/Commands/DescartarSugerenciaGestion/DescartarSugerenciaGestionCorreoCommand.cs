using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.DescartarSugerenciaGestion;

/// <summary>
/// El Gestor decide que este ítem no aplica (falso positivo, o ya se gestionó a mano) — lo retira
/// de la Bandeja sin crear ninguna Gestion. <paramref name="Id"/> es el ítem
/// (DetalleSugerenciaGestionCorreo), no la sugerencia completa: un correo puede sugerir varios
/// ítems a la vez, y descartar uno no afecta al resto.
/// </summary>
public record DescartarSugerenciaGestionCorreoCommand(Guid Id) : ICommand;

public class DescartarSugerenciaGestionCorreoCommandHandler(
    IDetalleSugerenciaGestionCorreoRepository repositorio, ICurrentUserService currentUserService, IUnitOfWork unitOfWork)
    : IRequestHandler<DescartarSugerenciaGestionCorreoCommand, Result>
{
    public async Task<Result> Handle(DescartarSugerenciaGestionCorreoCommand request, CancellationToken cancellationToken)
    {
        var detalle = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (detalle is null)
            return Result.Fallo(Error.Crear("SugerenciaGestionCorreo.NoEncontrada", "No encontramos esta sugerencia."));

        detalle.Resolver(ResolucionSugerencia.Descartada, await currentUserService.ObtenerUsuarioActualIdAsync());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
