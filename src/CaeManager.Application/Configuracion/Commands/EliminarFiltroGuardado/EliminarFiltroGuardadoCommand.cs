using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;

public record EliminarFiltroGuardadoCommand(Guid Id) : ICommand, IComandoDeAutoservicio;

public class EliminarFiltroGuardadoCommandValidator : AbstractValidator<EliminarFiltroGuardadoCommand>
{
    public EliminarFiltroGuardadoCommandValidator() => RuleFor(c => c.Id).NotEmpty();
}

public class EliminarFiltroGuardadoCommandHandler(
    ICurrentUserService currentUserService, ITenantActual tenantActual,
    IFiltroGuardadoRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarFiltroGuardadoCommand, Result>
{
    public async Task<Result> Handle(EliminarFiltroGuardadoCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("FiltroGuardado.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        var filtro = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);

        // Solo se puede borrar lo que se puede ver: el filtro tiene que ser del
        // usuario actual Y del Tenant activo (misma clave con la que se lee, ver
        // PantallasConFiltrosGuardados.EsDelTenant). Sin Tenant resuelto, o con
        // una fila antigua sin Tenant en la clave, no se borra nada.
        //
        // Mismo mensaje para "no existe", "no es tuyo" y "es de otro Tenant": no
        // revela si el filtro existe.
        if (filtro is null || filtro.UsuarioId != usuarioId.Value
            || !PantallasConFiltrosGuardados.EsDelTenant(filtro.Pantalla, tenantActual.TenantId))
            return Result.Fallo(Error.Crear("FiltroGuardado.NoEncontrado", "No encontramos ese filtro guardado."));

        repositorio.Eliminar(filtro);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
