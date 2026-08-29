using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignaciones;

/// <summary>
/// Versión en lote de <see cref="DarDeBajaAsignacion.DarDeBajaAsignacionCommand"/>
/// — la usa "Dar de baja seleccionados" en el acordeón de asignaciones de
/// <c>/centros</c> (Centro 360, <c>PLAN-EJECUCION-UX.md</c> § 0.1). No es un
/// borrado (ver el comentario del Command singular): fija
/// <c>FechaBaja</c>, conserva el historial. Por eso el DTO de resultado no
/// reutiliza <c>ResultadoEliminacionLoteDto</c> — esa palabra es de
/// Eliminar/soft-delete, y una Asignación nunca se elimina.
/// </summary>
public record DarDeBajaAsignacionesCommand(IReadOnlyList<Guid> Ids, DateOnly FechaBaja) : ICommand<ResultadoBajaLoteDto>;

public record ResultadoBajaLoteDto(int DadasDeBaja, IReadOnlyList<string> Errores);

public class DarDeBajaAsignacionesCommandValidator : AbstractValidator<DarDeBajaAsignacionesCommand>
{
    public DarDeBajaAsignacionesCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class DarDeBajaAsignacionesCommandHandler(
    IAsignacionRepository repositorio, IAutoridadAsignacionesService autoridad, IUnitOfWork unitOfWork)
    : IRequestHandler<DarDeBajaAsignacionesCommand, Result<ResultadoBajaLoteDto>>
{
    public async Task<Result<ResultadoBajaLoteDto>> Handle(DarDeBajaAsignacionesCommand request, CancellationToken cancellationToken)
    {
        var dadasDeBaja = 0;
        var errores = new List<string>();

        // Se resuelven primero todas las asignaciones del lote para preguntar
        // por sus centros de una vez: una llamada de autoridad por asignacion
        // repetiria la resolucion de cartera en cada vuelta.
        var asignaciones = new List<Domain.Asignaciones.Asignacion>();
        foreach (var id in request.Ids)
        {
            var encontrada = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (encontrada is null)
            {
                errores.Add("Una asignación ya no existía.");
                continue;
            }

            asignaciones.Add(encontrada);
        }

        // Misma autoridad que el alta (decision del propietario, 2026-08-29):
        // la baja no es una excepcion por ser reversible. Antes este bucle
        // daba de baja cualquier asignacion cuyo Id se conociera.
        var centrosConAutoridad = (await autoridad.FiltrarCentrosConAutoridadAsync(
                asignaciones.Select(a => a.CentroId).Distinct().ToList(), cancellationToken))
            .ToHashSet();

        foreach (var asignacion in asignaciones)
        {
            if (!centrosConAutoridad.Contains(asignacion.CentroId))
            {
                // Mismo texto que "ya no existia": no se confirma la existencia
                // de una asignacion fuera del ambito de quien pregunta.
                errores.Add("Una asignación ya no existía.");
                continue;
            }

            try
            {
                asignacion.DarDeBaja(request.FechaBaja);
                dadasDeBaja++;
            }
            catch (ArgumentException ex)
            {
                errores.Add(ex.Message);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoBajaLoteDto(dadasDeBaja, errores));
    }
}
