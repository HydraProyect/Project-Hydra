using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;

public record EliminarFiltroGuardadoCommand(Guid Id) : ICommand;

public class EliminarFiltroGuardadoCommandValidator : AbstractValidator<EliminarFiltroGuardadoCommand>
{
    public EliminarFiltroGuardadoCommandValidator() => RuleFor(c => c.Id).NotEmpty();
}

public class EliminarFiltroGuardadoCommandHandler(
    ICurrentUserService currentUserService, IFiltroGuardadoRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarFiltroGuardadoCommand, Result>
{
    public async Task<Result> Handle(EliminarFiltroGuardadoCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("FiltroGuardado.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        var filtro = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);

        // Mismo mensaje para "no existe" y "no es tuyo": no revela si el filtro
        // de otro usuario existe o no.
        if (filtro is null || filtro.UsuarioId != usuarioId.Value)
            return Result.Fallo(Error.Crear("FiltroGuardado.NoEncontrado", "No encontramos ese filtro guardado."));

        repositorio.Eliminar(filtro);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
