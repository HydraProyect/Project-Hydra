using CaeManager.Application.Common;
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

public record ResultadoAsignacionLoteDto(int Creadas, int YaActivas, int Solapadas, IReadOnlyList<string> Errores);

public class CrearAsignacionesCommandValidator : AbstractValidator<CrearAsignacionesCommand>
{
    // Auditoría Módulo 5, hallazgo crítico 10/9: sin tope, una petición con
    // decenas de miles de combinaciones puede agotar RAM rastreando entidades
    // en EF, saturar PostgreSQL o forzar un rollback completo por duplicados
    // internos. Los topes son generosos para el uso real (alta por Excel/
    // matriz) y acotan el peor caso, no el caso típico.
    public const int MaximoTrabajadoresPorLote = 200;
    public const int MaximoCentrosPorLote = 200;
    public const int MaximoCombinacionesPorLote = 2000;

    public CrearAsignacionesCommandValidator()
    {
        RuleFor(c => c.TrabajadorIds).NotEmpty().WithMessage("Selecciona al menos un trabajador.");
        RuleFor(c => c.CentroIds).NotEmpty().WithMessage("Selecciona al menos un centro.");

        RuleFor(c => c.TrabajadorIds.Distinct().Count())
            .LessThanOrEqualTo(MaximoTrabajadoresPorLote)
            .WithMessage($"Selecciona como máximo {MaximoTrabajadoresPorLote} trabajadores por alta.");
        RuleFor(c => c.CentroIds.Distinct().Count())
            .LessThanOrEqualTo(MaximoCentrosPorLote)
            .WithMessage($"Selecciona como máximo {MaximoCentrosPorLote} centros por alta.");
        RuleFor(c => c)
            .Must(c => (long)c.TrabajadorIds.Distinct().Count() * c.CentroIds.Distinct().Count() <= MaximoCombinacionesPorLote)
            .WithMessage($"La combinación de trabajadores y centros supera el máximo de {MaximoCombinacionesPorLote} altas por lote.");
    }
}

public class CrearAsignacionesCommandHandler(
    IAsignacionRepository repositorio, IAsignacionesQueryContext asignacionesContext,
    IAutoridadAsignacionesService autoridad, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearAsignacionesCommand, Result<ResultadoAsignacionLoteDto>>
{
    public async Task<Result<ResultadoAsignacionLoteDto>> Handle(CrearAsignacionesCommand request, CancellationToken cancellationToken)
    {
        // Deduplicado antes de tocar la base: la matriz de alta puede enviar
        // el mismo Id repetido (varias columnas marcando el mismo trabajador),
        // y sin esto el producto cartesiano se calcula sobre listas infladas.
        var trabajadorIdsSolicitados = request.TrabajadorIds.Distinct().ToList();
        var centroIdsSolicitados = request.CentroIds.Distinct().ToList();

        // Autoridad sobre cada trabajador, no solo existencia (auditoría
        // Módulo 5, hallazgo crítico 6/9) — ver CrearAsignacionCommand.
        var trabajadorIdsValidos = await autoridad.FiltrarTrabajadoresConAutoridadAsync(
            trabajadorIdsSolicitados, cancellationToken);

        // Autoridad sobre cada centro, no solo existencia (decision del
        // propietario, 2026-08-29). Filtra igual que antes filtraba la
        // existencia -descartando en silencio del lote- porque el mensaje que
        // sigue ya cuenta cuantos quedaron fuera: un centro ajeno se comporta
        // como uno que ya no existe, y no se confirma cual de las dos cosas es.
        var centroIdsValidos = await autoridad.FiltrarCentrosConAutoridadAsync(
            centroIdsSolicitados, cancellationToken);

        var errores = new List<string>();
        var trabajadoresFaltantes = trabajadorIdsSolicitados.Count - trabajadorIdsValidos.Count;
        if (trabajadoresFaltantes > 0)
            errores.Add($"{trabajadoresFaltantes} trabajador(es) ya no existían.");

        var centrosFaltantes = centroIdsSolicitados.Count - centroIdsValidos.Count;
        if (centrosFaltantes > 0)
            errores.Add($"{centrosFaltantes} centro(s) ya no existían.");

        // Una sola consulta para todo el lote en vez de un ExisteActivaAsync
        // (o ExisteSolapeAsync) por combinación — con selecciones de decenas
        // de trabajadores esto evita cientos de ida-y-vueltas a la base de
        // datos. Trae TODAS las filas del par, activas o cerradas: DEC-19
        // exige comprobar solape también contra las cerradas, no solo si hay
        // una activa.
        var existentes = await asignacionesContext.Asignaciones
            .Where(a => trabajadorIdsValidos.Contains(a.TrabajadorId) && centroIdsValidos.Contains(a.CentroId))
            .Select(a => new { a.TrabajadorId, a.CentroId, a.FechaAlta, a.FechaBaja })
            .ToListAsync(cancellationToken);

        var yaActivasSet = new HashSet<(Guid, Guid)>();
        var solapadasSet = new HashSet<(Guid, Guid)>();
        foreach (var existente in existentes)
        {
            var clave = (existente.TrabajadorId, existente.CentroId);
            if (existente.FechaBaja is null)
            {
                yaActivasSet.Add(clave);
                continue;
            }

            // Rango vacío (FechaAlta == FechaBaja, ver Asignacion.SeSolapaCon):
            // no ocupó ni un día, así que no puede solapar con nada.
            if (existente.FechaBaja.Value == existente.FechaAlta) continue;

            // Mismo límite exclusivo que Asignacion.SeSolapaCon: el alta
            // nueva es un rango abierto [FechaAlta, ∞), así que solapa con
            // una fila cerrada exactamente cuando su alta es anterior a la
            // baja de esa fila.
            if (request.FechaAlta < existente.FechaBaja.Value)
                solapadasSet.Add(clave);
        }

        var creadas = 0;
        var yaActivas = 0;
        var solapadas = 0;

        foreach (var trabajadorId in trabajadorIdsValidos)
        {
            foreach (var centroId in centroIdsValidos)
            {
                var clave = (trabajadorId, centroId);
                if (yaActivasSet.Contains(clave))
                {
                    yaActivas++;
                    continue;
                }

                if (solapadasSet.Contains(clave))
                {
                    solapadas++;
                    continue;
                }

                repositorio.Agregar(new Asignacion(trabajadorId, centroId, request.FechaAlta));
                creadas++;
            }
        }

        if (solapadas > 0)
            errores.Add(
                $"{solapadas} combinación(es) se omitieron: la fecha de alta solapa con un periodo ya registrado (activo o cerrado) para ese mismo trabajador y centro.");

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoAsignacionLoteDto(creadas, yaActivas, solapadas, errores));
    }
}
