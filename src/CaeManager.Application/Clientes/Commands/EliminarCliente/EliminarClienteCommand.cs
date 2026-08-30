using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using MediatR;

namespace CaeManager.Application.Clientes.Commands.EliminarCliente;

public record EliminarClienteCommand(Guid Id) : ICommand;

public class EliminarClienteCommandHandler(
    IEmpresaRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService)
    : IRequestHandler<EliminarClienteCommand, Result>
{
    public async Task<Result> Handle(EliminarClienteCommand request, CancellationToken cancellationToken)
    {
        var empresa = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (empresa is null || !await alcanceDatos.ClienteVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));

        if (await repositorio.TieneCentrosComoTitularAsync(request.Id, cancellationToken))
            return Result.Fallo(Error.Crear(
                "Cliente.TieneCentrosActivos",
                "No puedes eliminar un cliente con centros activos. Da de baja sus centros primero."));

        // Auditoría Módulo 5, hallazgo crítico 7/9 — ver EliminarCentroCommand.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Cliente.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        empresa.MarcarComoEliminado(usuarioId.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
