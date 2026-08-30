using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Trabajadores.Commands.EliminarTrabajadores;

/// <summary>Borrado en lote (P3-31) — ver EliminarClientesCommand para el criterio de éxito parcial.</summary>
public record EliminarTrabajadoresCommand(IReadOnlyList<Guid> Ids) : ICommand<ResultadoEliminacionLoteDto>;

public class EliminarTrabajadoresCommandValidator : AbstractValidator<EliminarTrabajadoresCommand>
{
    public EliminarTrabajadoresCommandValidator() => RuleFor(c => c.Ids).NotEmpty();
}

public class EliminarTrabajadoresCommandHandler(
    ITrabajadorRepository repositorio,
    IAsignacionRepository asignaciones,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService)
    : IRequestHandler<EliminarTrabajadoresCommand, Result<ResultadoEliminacionLoteDto>>
{
    public async Task<Result<ResultadoEliminacionLoteDto>> Handle(EliminarTrabajadoresCommand request, CancellationToken cancellationToken)
    {
        // Auditoría Módulo 5, hallazgo crítico 7/9 — ver EliminarCentroCommand.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo<ResultadoEliminacionLoteDto>(Error.Crear("Trabajador.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        var eliminados = 0;
        var errores = new List<string>();

        foreach (var id in request.Ids)
        {
            var trabajador = await repositorio.ObtenerPorIdAsync(id, cancellationToken);
            if (trabajador is null || !await alcanceDatos.TrabajadorVisibleAsync(trabajador.Id, cancellationToken))
            {
                errores.Add("Un trabajador ya no existía.");
                continue;
            }

            trabajador.MarcarComoEliminado(usuarioId.Value);
            await CierreDeAsignaciones.PorTrabajadorEliminadoAsync(asignaciones, trabajador.Id, cancellationToken);
            eliminados++;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(new ResultadoEliminacionLoteDto(eliminados, errores));
    }
}
