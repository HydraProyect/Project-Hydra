using CaeManager.Application.Common;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Clientes.Commands.EliminarCliente;

public record EliminarClienteCommand(Guid Id, Guid UsuarioId) : ICommand;

public class EliminarClienteCommandHandler(IClienteRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarClienteCommand, Result>
{
    public async Task<Result> Handle(EliminarClienteCommand request, CancellationToken cancellationToken)
    {
        var cliente = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (cliente is null)
            return Result.Fallo(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));

        if (await repositorio.TieneCentrosActivosAsync(request.Id, cancellationToken))
            return Result.Fallo(Error.Crear(
                "Cliente.TieneCentrosActivos",
                "No puedes eliminar un cliente con centros activos. Da de baja sus centros primero."));

        cliente.MarcarComoEliminado(request.UsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
