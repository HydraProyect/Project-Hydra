using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Commands.MarcarDocumentacionGestionada;

/// <summary>
/// Salida manual de «Por gestionar» (decisión del propietario, 2026-10-09): da por gestionada
/// la documentación de una Visita sin enviar el paquete desde TALVEG —lo subido al portal del
/// titular del Centro, lo enviado desde otro correo—. La otra salida es
/// <c>EnviarPaqueteAcreditacionVisitaCommand</c>.
/// <list type="bullet">
/// <item>Autorización: la de «Avisada», añadir un Trabajador y editar — rol que escribe
/// (<c>ICommand</c>) y alcance de GESTIÓN sobre el Centro de la Visita. Fuera de alcance,
/// lo mismo que una Visita inexistente.</item>
/// <item>No exige que los documentos estén vigentes: la vigencia la confirma quien gestiona,
/// y la pantalla sigue mostrando el estado de cada documento.</item>
/// <item>Lleva la versión que se vio en pantalla: si alguien añadió o quitó un Trabajador
/// mientras tanto, no se marca gestionado lo que no se vio.</item>
/// </list>
/// </summary>
public record MarcarDocumentacionGestionadaCommand(Guid Id, Guid Version = default) : ICommand;

public class MarcarDocumentacionGestionadaCommandValidator : AbstractValidator<MarcarDocumentacionGestionadaCommand>
{
    public MarcarDocumentacionGestionadaCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
    }
}

public class MarcarDocumentacionGestionadaCommandHandler(
    IVisitaRepository repositorio, ICentrosQueryContext centrosContext, IUnitOfWork unitOfWork, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<MarcarDocumentacionGestionadaCommand, Result>
{
    public static readonly Error CentroSinGestionCae = Error.Crear(
        "Visita.CentroSinGestionCae",
        "Este Centro no requiere gestión CAE: no hay documentación que gestionar para esta visita.");

    public async Task<Result> Handle(MarcarDocumentacionGestionadaCommand request, CancellationToken cancellationToken)
    {
        // Mismo orden que al editar: alcance de gestión antes que nada, para no revelar
        // qué hay fuera ni su versión; después cancelada y concurrencia.
        var visita = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (visita is null || !await AutorizacionCancelacionVisita.PuedeGestionarAsync(alcanceDatos, visita, cancellationToken))
            return Result.Fallo(AutorizacionCancelacionVisita.NoEncontrada);

        // FS-11: una Visita cancelada no se modifica; primero se reactiva.
        if (visita.EstaCancelada)
            return Result.Fallo(Error.Crear("Visita.Cancelada", "Esta visita está cancelada. Reactívala antes de modificarla."));

        if (ConcurrenciaOptimista.Verificar(visita, request.Version, "esta visita") is { } conflicto)
            return Result.Fallo(conflicto);

        // P1-X2: en un Centro sin gestión CAE la columna no dice ni «por gestionar» ni
        // «gestionada»; una marca ahí no tendría dónde verse.
        var gestionCae = await centrosContext.Centros
            .Where(c => c.Id == visita.CentroId)
            .Select(c => (ModalidadGestionCae?)c.GestionCae)
            .FirstOrDefaultAsync(cancellationToken);
        if (gestionCae is null or ModalidadGestionCae.SinGestionCae)
            return Result.Fallo(CentroSinGestionCae);

        visita.MarcarDocumentacionGestionada(DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
