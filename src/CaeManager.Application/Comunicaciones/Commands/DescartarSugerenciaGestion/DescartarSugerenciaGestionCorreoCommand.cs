using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Commands.DescartarSugerenciaGestion;

/// <summary>
/// El Gestor decide que este ítem no aplica (falso positivo, o ya se gestionó a mano) — lo retira
/// de la Bandeja sin crear ninguna Gestion. <paramref name="Id"/> es el ítem
/// (DetalleSugerenciaGestionCorreo), no la sugerencia completa: un correo puede sugerir varios
/// ítems a la vez, y descartar uno no afecta al resto.
///
/// Asignación de Cartera: el ítem solo se descarta si quien mira alcanza la Conversación del
/// mensaje que lo originó, el mismo criterio con el que <c>ObtenerConversacionPorIdQuery</c> se
/// lo enseña. Fuera de alcance responde igual que un ítem que no existe, para no delatarlo.
/// </summary>
public record DescartarSugerenciaGestionCorreoCommand(Guid Id) : ICommand;

public class DescartarSugerenciaGestionCorreoCommandHandler(
    IDetalleSugerenciaGestionCorreoRepository repositorio, IComunicacionesQueryContext comunicacionesContext,
    IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService, IUnitOfWork unitOfWork)
    : IRequestHandler<DescartarSugerenciaGestionCorreoCommand, Result>
{
    private static readonly Error NoEncontrada =
        Error.Crear("SugerenciaGestionCorreo.NoEncontrada", "No encontramos esta sugerencia.");

    public async Task<Result> Handle(DescartarSugerenciaGestionCorreoCommand request, CancellationToken cancellationToken)
    {
        var mensajeId = await (
            from detalle in comunicacionesContext.DetallesSugerenciaGestionCorreo
            where detalle.Id == request.Id
            join sugerencia in comunicacionesContext.SugerenciasGestionCorreo on detalle.SugerenciaGestionCorreoId equals sugerencia.Id
            select (Guid?)sugerencia.MensajeId)
            .FirstOrDefaultAsync(cancellationToken);

        if (mensajeId is null || !await alcanceDatos.MensajeVisibleAsync(comunicacionesContext, mensajeId.Value, cancellationToken))
            return Result.Fallo(NoEncontrada);

        var detalleSugerido = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (detalleSugerido is null)
            return Result.Fallo(NoEncontrada);

        detalleSugerido.Resolver(ResolucionSugerencia.Descartada, await currentUserService.ObtenerUsuarioActualIdAsync());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
