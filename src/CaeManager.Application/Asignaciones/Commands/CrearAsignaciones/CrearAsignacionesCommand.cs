using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;

/// <summary>
/// Versión en lote de <see cref="CrearAsignacion.CrearAsignacionCommand"/> —
/// da de alta el producto cartesiano de Trabajadores × Centros (N
/// trabajadores a 1 centro, 1 trabajador a N centros, o ambos a la vez). Es
/// exactamente la forma del Excel original (matriz Trabajador × Centro,
/// ver <c>DATABASE.md</c>), y el vehículo tanto del Drawer de alta múltiple
/// como de la vista matriz de <c>/asignaciones</c> — la matriz llama a este
/// Command una vez por columna (un Centro, los Trabajadores recién marcados
/// en esa columna), que sigue siendo un producto cartesiano válido.
///
/// Una combinación ya activa se omite en silencio (<see cref="ResultadoAsignacionLoteDto.YaActivas"/>)
/// — no es un error, es lo esperable al reintentar una selección que se
/// solapa con lo que ya había.
/// </summary>
public record CrearAsignacionesCommand(
    IReadOnlyList<Guid> TrabajadorIds, IReadOnlyList<Guid> CentroIds, DateOnly FechaAlta) : ICommand<ResultadoAsignacionLoteDto>;

public record ResultadoAsignacionLoteDto(int Creadas, int YaActivas, IReadOnlyList<string> Errores);

public class CrearAsignacionesCommandValidator : AbstractValidator<CrearAsignacionesCommand>
{
    public CrearAsignacionesCommandValidator()
    {
        RuleFor(c => c.TrabajadorIds).NotEmpty().WithMessage("Selecciona al menos un trabajador.");
        RuleFor(c => c.CentroIds).NotEmpty().WithMessage("Selecciona al menos un centro.");
    }
}

public class CrearAsignacionesCommandHandler(
    IAsignacionRepository repositorio, IAsignacionesQueryContext asignacionesContext,
    ITrabajadoresQueryContext trabajadoresContext, IAutoridadAsignacionesService autoridad, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearAsignacionesCommand, Result<ResultadoAsignacionLoteDto>>
{
    public async Task<Result<ResultadoAsignacionLoteDto>> Handle(CrearAsignacionesCommand request, CancellationToken cancellationToken)
    {
        // Mismo motivo que CrearAsignacionCommand (P0-1 de docs/business/MATURITY_REVIEW.md):
        // el filtro de tenant ya deja "no encontrado" un Id ajeno — se
        // descarta en silencio del lote en vez de fallar el lote entero por
        // un Id que otro usuario borró mientras el formulario estaba abierto.
        var trabajadorIdsValidos = await trabajadoresContext.Trabajadores
            .Where(t => request.TrabajadorIds.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);

        // Autoridad sobre cada centro, no solo existencia (decision del
        // propietario, 2026-08-29). Filtra igual que antes filtraba la
        // existencia -descartando en silencio del lote- porque el mensaje que
        // sigue ya cuenta cuantos quedaron fuera: un centro ajeno se comporta
        // como uno que ya no existe, y no se confirma cual de las dos cosas es.
        var centroIdsValidos = await autoridad.FiltrarCentrosConAutoridadAsync(
            request.CentroIds, cancellationToken);

        var errores = new List<string>();
        var trabajadoresFaltantes = request.TrabajadorIds.Count - trabajadorIdsValidos.Count;
        if (trabajadoresFaltantes > 0)
            errores.Add($"{trabajadoresFaltantes} trabajador(es) ya no existían.");

        var centrosFaltantes = request.CentroIds.Count - centroIdsValidos.Count;
        if (centrosFaltantes > 0)
            errores.Add($"{centrosFaltantes} centro(s) ya no existían.");

        // Una sola consulta para todo el lote en vez de un ExisteActivaAsync
        // por combinación — con selecciones de decenas de trabajadores esto
        // evita cientos de ida-y-vueltas a la base de datos.
        var activasExistentes = await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null
                && trabajadorIdsValidos.Contains(a.TrabajadorId)
                && centroIdsValidos.Contains(a.CentroId))
            .Select(a => new { a.TrabajadorId, a.CentroId })
            .ToListAsync(cancellationToken);

        var yaActivasSet = activasExistentes.Select(a => (a.TrabajadorId, a.CentroId)).ToHashSet();

        var creadas = 0;
        var yaActivas = 0;

        foreach (var trabajadorId in trabajadorIdsValidos)
        {
            foreach (var centroId in centroIdsValidos)
            {
                if (yaActivasSet.Contains((trabajadorId, centroId)))
                {
                    yaActivas++;
                    continue;
                }

                repositorio.Agregar(new Asignacion(trabajadorId, centroId, request.FechaAlta));
                creadas++;
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoAsignacionLoteDto(creadas, yaActivas, errores));
    }
}
