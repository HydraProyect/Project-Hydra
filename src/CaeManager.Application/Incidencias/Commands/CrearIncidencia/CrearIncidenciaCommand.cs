using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Incidencias;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Incidencias.Commands.CrearIncidencia;

public record CrearIncidenciaCommand(
    Guid CentroId, Guid? TrabajadorId, TipoIncidencia Tipo, GravedadIncidencia Gravedad,
    DateOnly FechaOcurrencia, string Descripcion) : ICommand<Guid>;

public class CrearIncidenciaCommandValidator : AbstractValidator<CrearIncidenciaCommand>
{
    public CrearIncidenciaCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty().WithMessage("Selecciona un centro.");
        RuleFor(c => c.Descripcion)
            .NotEmpty().WithMessage("Describe qué ocurrió.")
            .MaximumLength(Incidencia.LongitudMaximaDescripcion);
    }
}

public class CrearIncidenciaCommandHandler(
    IIncidenciaRepository repositorio, ICentrosQueryContext centrosContext, ITrabajadoresQueryContext trabajadoresContext,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearIncidenciaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearIncidenciaCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de Project-Hydra-Negocio/MATURITY_REVIEW.md —, y
        // Asignación de Cartera: la Incidencia pertenece a su Centro, y fuera del alcance de gestión
        // ese Centro «no existe» (mismo código que un Id inexistente).
        if (!await centrosContext.Centros.AnyAsync(c => c.Id == request.CentroId, cancellationToken)
            || !await alcanceDatos.CentroParaGestionVisibleAsync(request.CentroId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Incidencia.CentroNoEncontrado", "No encontramos este centro."));

        // El Trabajador implicado solo se verifica dentro del Tenant, no contra la cartera: la
        // pantalla lo elige de la base general del Tenant (AlcanceSelectorTrabajadores.BaseGeneralDelTenant),
        // y la Incidencia pertenece al Centro, que es lo que la cartera acota arriba.
        if (request.TrabajadorId is { } trabajadorId
            && !await trabajadoresContext.Trabajadores.AnyAsync(t => t.Id == trabajadorId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Incidencia.TrabajadorNoEncontrado", "No encontramos este trabajador."));

        var incidencia = new Incidencia(
            request.CentroId, request.TrabajadorId, request.Tipo, request.Gravedad,
            request.FechaOcurrencia, request.Descripcion);

        repositorio.Agregar(incidencia);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(incidencia.Id);
    }
}
