using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using MediatR;

namespace CaeManager.Application.Empresas.Commands.BorrarCredencialAccesoEmpresaContrasena;

/// <summary>
/// El acto explícito de borrar la contraseña que DEC-62 exige: desde que
/// <c>GuardarCredencialAccesoEmpresaCommand</c> conserva la contraseña
/// almacenada cuando el campo llega vacío, ya no hay ninguna otra forma de
/// borrarla — esta es la única.
/// </summary>
public record BorrarCredencialAccesoEmpresaContrasenaCommand(Guid EmpresaId) : ICommand;

public class BorrarCredencialAccesoEmpresaContrasenaCommandHandler(
    IEmpresaRepository empresaRepositorio,
    ICredencialAccesoEmpresaRepository credencialRepositorio,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<BorrarCredencialAccesoEmpresaContrasenaCommand, Result>
{
    public async Task<Result> Handle(BorrarCredencialAccesoEmpresaContrasenaCommand request, CancellationToken cancellationToken)
    {
        var empresa = await empresaRepositorio.ObtenerPorIdAsync(request.EmpresaId, cancellationToken);
        if (empresa is null || !await alcanceDatos.EmpresaParaGestionVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa."));

        var credencial = await credencialRepositorio.ObtenerPorEmpresaAsync(request.EmpresaId, cancellationToken);
        if (credencial is null)
            return Result.Exito();

        credencial.BorrarContrasena();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
