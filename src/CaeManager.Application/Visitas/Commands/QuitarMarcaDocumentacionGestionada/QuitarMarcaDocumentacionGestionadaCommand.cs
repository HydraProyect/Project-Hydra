using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Visitas.Commands.QuitarMarcaDocumentacionGestionada;

/// <summary>
/// Deshace la marca de documentación gestionada (petición del propietario, 2026-10-09): la
/// Visita vuelve a «Por gestionar». Hasta ahora solo la borraba cambiar los Trabajadores, así
/// que una marca puesta por error no tenía arreglo directo.
/// <list type="bullet">
/// <item>Autorización: la misma que para marcar — rol que escribe (<c>ICommand</c>) y alcance
/// de GESTIÓN sobre el Centro de la Visita. Fuera de alcance, lo mismo que una Visita
/// inexistente.</item>
/// <item>La Visita no guarda cómo se marcó, así que quita la marca venga de la acción manual o
/// del envío del paquete. El paquete ya enviado no se toca: sigue en su conversación.</item>
/// <item>Sobre una Visita que ya está por gestionar no hace nada y responde con éxito.</item>
/// <item>Lleva la versión que se vio en pantalla, igual que la marca.</item>
/// </list>
/// </summary>
public record QuitarMarcaDocumentacionGestionadaCommand(Guid Id, Guid Version = default) : ICommand;

public class QuitarMarcaDocumentacionGestionadaCommandValidator : AbstractValidator<QuitarMarcaDocumentacionGestionadaCommand>
{
    public QuitarMarcaDocumentacionGestionadaCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public class QuitarMarcaDocumentacionGestionadaCommandHandler(
    IVisitaRepository repositorio, IUnitOfWork unitOfWork, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<QuitarMarcaDocumentacionGestionadaCommand, Result>
{
    public async Task<Result> Handle(QuitarMarcaDocumentacionGestionadaCommand request, CancellationToken cancellationToken)
    {
        // Mismo orden que al marcar: alcance de gestión antes que nada, para no revelar
        // qué hay fuera ni su versión; después cancelada y concurrencia.
        var visita = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (visita is null || !await AutorizacionCancelacionVisita.PuedeGestionarAsync(alcanceDatos, visita, cancellationToken))
            return Result.Fallo(AutorizacionCancelacionVisita.NoEncontrada);

        // FS-11: una Visita cancelada no se modifica; primero se reactiva.
        if (visita.EstaCancelada)
            return Result.Fallo(Error.Crear("Visita.Cancelada", "Esta visita está cancelada. Reactívala antes de modificarla."));

        if (ConcurrenciaOptimista.Verificar(visita, request.Version, "esta visita") is { } conflicto)
            return Result.Fallo(conflicto);

        if (!visita.DocumentacionGestionada)
            return Result.Exito();

        visita.QuitarMarcaDocumentacionGestionada();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
