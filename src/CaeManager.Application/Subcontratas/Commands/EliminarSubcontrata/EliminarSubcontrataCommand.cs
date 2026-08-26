using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using MediatR;

namespace CaeManager.Application.Subcontratas.Commands.EliminarSubcontrata;

public record EliminarSubcontrataCommand(Guid Id, Guid UsuarioId) : ICommand;

public class EliminarSubcontrataCommandHandler(IEmpresaRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
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

        subcontrata.MarcarComoEliminado(request.UsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
