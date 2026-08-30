using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using MediatR;

namespace CaeManager.Application.Subcontratas.Commands.EliminarSubcontrata;

public record EliminarSubcontrataCommand(Guid Id) : ICommand;

public class EliminarSubcontrataCommandHandler(
    IEmpresaRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService)
    : IRequestHandler<EliminarSubcontrataCommand, Result>
{
    public async Task<Result> Handle(EliminarSubcontrataCommand request, CancellationToken cancellationToken)
    {
        var subcontrata = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (subcontrata is null || !await alcanceDatos.SubcontrataVisibleAsync(subcontrata.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Subcontrata.NoEncontrada", "No encontramos esta subcontrata."));

        if (await repositorio.TieneTrabajadoresComoSubcontrataAsync(request.Id, cancellationToken))
            return Result.Fallo(Error.Crear(
                "Subcontrata.TieneTrabajadores",
                "No puedes eliminar una subcontrata con trabajadores. Da de baja a sus trabajadores primero."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Subcontrata.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        subcontrata.MarcarComoEliminado(usuarioId.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
