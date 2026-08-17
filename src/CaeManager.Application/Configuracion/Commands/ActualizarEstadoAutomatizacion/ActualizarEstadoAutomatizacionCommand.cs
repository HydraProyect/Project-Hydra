using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Configuracion.Commands.ActualizarEstadoAutomatizacion;

public record ActualizarEstadoAutomatizacionCommand(string TrabajoId, bool Activo) : ICommand;

public class ActualizarEstadoAutomatizacionCommandValidator : AbstractValidator<ActualizarEstadoAutomatizacionCommand>
{
    public ActualizarEstadoAutomatizacionCommandValidator()
    {
        RuleFor(c => c.TrabajoId).NotEmpty();
    }
}

public class ActualizarEstadoAutomatizacionCommandHandler(IEstadoAutomatizacionRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<ActualizarEstadoAutomatizacionCommand, Result>
{
    public async Task<Result> Handle(ActualizarEstadoAutomatizacionCommand request, CancellationToken cancellationToken)
    {
        var definicion = CatalogoAutomatizaciones.Trabajos.FirstOrDefault(t => t.Id == request.TrabajoId);
        if (definicion is null)
            return Result.Fallo(Error.Crear("Automatizacion.NoEncontrada", "No reconocemos este trabajo."));

        if (!definicion.Conmutable)
            return Result.Fallo(Error.Crear(
                "Automatizacion.NoConmutable", $"«{definicion.Nombre}» es global para todos los tenants y no se puede apagar por separado."));

        var estado = await repositorio.ObtenerPorTrabajoAsync(request.TrabajoId, cancellationToken);
        if (estado is null)
        {
            estado = new EstadoAutomatizacion(request.TrabajoId);
            repositorio.Agregar(estado);
        }

        estado.CambiarActivo(request.Activo);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
