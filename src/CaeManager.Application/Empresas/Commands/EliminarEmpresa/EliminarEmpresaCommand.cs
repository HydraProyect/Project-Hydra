using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using MediatR;

namespace CaeManager.Application.Empresas.Commands.EliminarEmpresa;

public record EliminarEmpresaCommand(Guid Id, Guid UsuarioId) : IRequest<Result>;

public class EliminarEmpresaCommandHandler(IEmpresaRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarEmpresaCommand, Result>
{
    public async Task<Result> Handle(EliminarEmpresaCommand request, CancellationToken cancellationToken)
    {
        var empresa = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (empresa is null)
            return Result.Fallo(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa."));

        if (await repositorio.TieneTrabajadoresAsync(request.Id, cancellationToken))
            return Result.Fallo(Error.Crear(
                "Empresa.TieneTrabajadores",
                "No puedes eliminar una empresa con trabajadores. Da de baja a sus trabajadores primero."));

        empresa.MarcarComoEliminado(request.UsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
