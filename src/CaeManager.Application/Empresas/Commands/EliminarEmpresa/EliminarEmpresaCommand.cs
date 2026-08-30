using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using MediatR;

namespace CaeManager.Application.Empresas.Commands.EliminarEmpresa;

public record EliminarEmpresaCommand(Guid Id) : ICommand;

public class EliminarEmpresaCommandHandler(
    IEmpresaRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService)
    : IRequestHandler<EliminarEmpresaCommand, Result>
{
    public async Task<Result> Handle(EliminarEmpresaCommand request, CancellationToken cancellationToken)
    {
        var empresa = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (empresa is null || !await alcanceDatos.EmpresaVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa."));

        if (await repositorio.TieneTrabajadoresAsync(request.Id, cancellationToken))
            return Result.Fallo(Error.Crear(
                "Empresa.TieneTrabajadores",
                "No puedes eliminar una empresa con trabajadores. Da de baja a sus trabajadores primero."));

        // Auditoría Módulo 5, hallazgo crítico 7/9 — ver EliminarCentroCommand.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Empresa.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        empresa.MarcarComoEliminado(usuarioId.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
