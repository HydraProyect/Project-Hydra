using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Asignaciones.Commands.CrearAsignacion;

public record CrearAsignacionCommand(Guid TrabajadorId, Guid CentroId, DateOnly FechaAlta) : ICommand<Guid>;

public class CrearAsignacionCommandValidator : AbstractValidator<CrearAsignacionCommand>
{
    public CrearAsignacionCommandValidator()
    {
        RuleFor(c => c.TrabajadorId).NotEmpty().WithMessage("Selecciona un trabajador.");
        RuleFor(c => c.CentroId).NotEmpty().WithMessage("Selecciona un centro.");
    }
}

public class CrearAsignacionCommandHandler(
    IAsignacionRepository repositorio, IAutoridadAsignacionesService autoridad, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearAsignacionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearAsignacionCommand request, CancellationToken cancellationToken)
    {
        // Autoridad sobre el trabajador, no solo existencia (auditoría Módulo
        // 5, hallazgo crítico 6/9): antes bastaba con que el trabajador
        // existiera en el tenant, así que un trabajador fuera de la cartera
        // del actor quedaba "secuestrado" hacia ella en cuanto se le
        // asignaba, exponiendo DNI, contacto y documentación médica. Mismo
        // error que "no encontrado" a propósito — ver PuedeModificarAsignacionesDelCentroAsync.
        if (!await autoridad.PuedeModificarAsignacionesDelTrabajadorAsync(request.TrabajadorId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Asignacion.TrabajadorNoEncontrado", "No encontramos este trabajador."));

        // Autoridad sobre el centro, no solo existencia (decision del
        // propietario, 2026-08-29): un gestor no asigna a un centro fuera de su
        // arbol. Antes bastaba con que el centro existiera en el tenant, asi
        // que cualquier gestor podia dar de alta a un trabajador en el centro
        // de otro con solo conocer su Id.
        //
        // Mismo error que "no encontrado" a proposito: distinguir <<no existe>>
        // de <<no es tuyo>> confirma la existencia de un centro ajeno.
        if (!await autoridad.PuedeModificarAsignacionesDelCentroAsync(request.CentroId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Asignacion.CentroNoEncontrado", "No encontramos este centro."));

        if (await repositorio.ExisteActivaAsync(request.TrabajadorId, request.CentroId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "Asignacion.YaActiva", "Este trabajador ya está dado de alta en este centro."));

        // DEC-19: dos vigencias solapadas del mismo trío son una
        // contradicción de datos, no solo dos filas simultáneamente
        // abiertas. Distinto de "YaActiva" (arriba): esto también atrapa un
        // alta nueva cuyo rango pisa el de una asignación YA CERRADA.
        if (await repositorio.ExisteSolapeAsync(request.TrabajadorId, request.CentroId, request.FechaAlta, null, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear(
                "Asignacion.SolapaConOtra",
                "Este trabajador ya tuvo una asignación a este centro cuyo periodo se solapa con esta fecha de alta."));

        var asignacion = new Asignacion(request.TrabajadorId, request.CentroId, request.FechaAlta);
        repositorio.Agregar(asignacion);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(asignacion.Id);
    }
}
