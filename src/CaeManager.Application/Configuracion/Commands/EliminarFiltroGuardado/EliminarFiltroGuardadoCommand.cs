using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;

/// <summary>
/// Elimina un filtro con nombre del usuario actual. Responde «no encontrado» por
/// igual si el Id no existe, si es de otro usuario, si se guardó en otro Tenant
/// (no lo ve el filtro global) o si es la fila reservada de la vista recordada.
/// </summary>
public record EliminarFiltroGuardadoCommand(Guid Id) : ICommand, IComandoDeAutoservicio;

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
        // de otro usuario existe o no. La vista recordada tampoco existe para este
        // comando, ni siquiera para su dueño: no es un filtro con nombre y solo la
        // borra OlvidarVistaRecordadaCommand.
        if (filtro is null || filtro.UsuarioId != usuarioId.Value || filtro.EsVistaRecordada)
            return Result.Fallo(Error.Crear("FiltroGuardado.NoEncontrado", "No encontramos ese filtro guardado."));

        repositorio.Eliminar(filtro);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
