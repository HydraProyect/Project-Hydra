using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Facturacion;
using MediatR;

namespace CaeManager.Application.Facturacion.Commands.EliminarTarifaCliente;

public record EliminarTarifaClienteCommand(Guid Id) : IRequest<Result>;

public class EliminarTarifaClienteCommandHandler(
    ITarifaClienteRepository repositorio,
    IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService)
    : IRequestHandler<EliminarTarifaClienteCommand, Result>
{
    public async Task<Result> Handle(EliminarTarifaClienteCommand request, CancellationToken cancellationToken)
    {
        var tarifa = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (tarifa is null)
            return Result.Fallo(Error.Crear("TarifaCliente.NoEncontrada", "La tarifa no existe o no tienes acceso."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        tarifa.MarcarComoEliminado(usuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
