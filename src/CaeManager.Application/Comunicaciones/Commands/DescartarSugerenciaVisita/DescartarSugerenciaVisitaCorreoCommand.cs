using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Commands.DescartarSugerenciaVisita;

/// <summary>
/// El Gestor decide que la sugerencia no aplica (falso positivo, o ya se gestionó a mano) — la retira de la Bandeja sin crear ninguna Visita.
///
/// Asignación de Cartera: solo se descarta si quien mira alcanza la Conversación del mensaje que la
/// originó, el mismo criterio con el que <c>ObtenerConversacionPorIdQuery</c> la enseña junto al botón
/// «Descartar». Fuera de alcance responde igual que una sugerencia que no existe, para no delatarla.
/// </summary>
public record DescartarSugerenciaVisitaCorreoCommand(Guid Id) : ICommand;

public class DescartarSugerenciaVisitaCorreoCommandHandler(
    ISugerenciaVisitaCorreoRepository repositorio, IComunicacionesQueryContext comunicacionesContext,
    IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService, IUnitOfWork unitOfWork)
    : IRequestHandler<DescartarSugerenciaVisitaCorreoCommand, Result>
{
    private static readonly Error NoEncontrada =
        Error.Crear("SugerenciaVisitaCorreo.NoEncontrada", "No encontramos esta sugerencia.");

    public async Task<Result> Handle(DescartarSugerenciaVisitaCorreoCommand request, CancellationToken cancellationToken)
    {
        var mensajeId = await comunicacionesContext.SugerenciasVisitaCorreo
            .Where(s => s.Id == request.Id)
            .Select(s => (Guid?)s.MensajeId)
            .FirstOrDefaultAsync(cancellationToken);

        if (mensajeId is null || !await alcanceDatos.MensajeVisibleAsync(comunicacionesContext, mensajeId.Value, cancellationToken))
            return Result.Fallo(NoEncontrada);

        var sugerencia = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (sugerencia is null)
            return Result.Fallo(NoEncontrada);

        sugerencia.Resolver(ResolucionSugerencia.Descartada, await currentUserService.ObtenerUsuarioActualIdAsync());
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
