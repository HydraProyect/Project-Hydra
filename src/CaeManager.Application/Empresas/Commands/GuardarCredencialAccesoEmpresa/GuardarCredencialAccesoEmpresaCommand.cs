using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Empresas.Commands.GuardarCredencialAccesoEmpresa;

/// <summary>
/// Alta o edición en un solo paso (upsert): una Empresa tiene como mucho un
/// juego de credenciales, así que no hace falta distinguir Crear/Editar
/// como en el resto de módulos — el propio handler decide si crea o
/// actualiza según exista o no ya un registro para esa Empresa.
/// </summary>
public record GuardarCredencialAccesoEmpresaCommand(
    Guid EmpresaId, string? UrlAcceso, string? CampoEmpresa, string? Usuario, string? Contrasena, string? Notas = null) : ICommand;

public class GuardarCredencialAccesoEmpresaCommandValidator : AbstractValidator<GuardarCredencialAccesoEmpresaCommand>
{
    public GuardarCredencialAccesoEmpresaCommandValidator()
    {
        RuleFor(c => c.EmpresaId).NotEmpty();
        RuleFor(c => c.UrlAcceso).MaximumLength(CredencialAccesoEmpresa.LongitudMaximaUrlAcceso);
        RuleFor(c => c.CampoEmpresa).MaximumLength(CredencialAccesoEmpresa.LongitudMaximaCampoEmpresa);
        RuleFor(c => c.Usuario).MaximumLength(CredencialAccesoEmpresa.LongitudMaximaUsuario);
        RuleFor(c => c.Contrasena).MaximumLength(CredencialAccesoEmpresa.LongitudMaximaContrasena);
        RuleFor(c => c.Notas).MaximumLength(CredencialAccesoEmpresa.LongitudMaximaNotas);
    }
}

public class GuardarCredencialAccesoEmpresaCommandHandler(
    IEmpresaRepository empresaRepositorio,
    ICredencialAccesoEmpresaRepository credencialRepositorio,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarCredencialAccesoEmpresaCommand, Result>
{
    public async Task<Result> Handle(GuardarCredencialAccesoEmpresaCommand request, CancellationToken cancellationToken)
    {
        var empresa = await empresaRepositorio.ObtenerPorIdAsync(request.EmpresaId, cancellationToken);
        // Defensa en profundidad (REC-149): inalcanzable para el rol Cliente
        // vía AutorizacionEscrituraBehavior; alcance de gestión como segunda
        // barrera independiente.
        if (empresa is null || !await alcanceDatos.EmpresaParaGestionVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa."));

        var credencial = await credencialRepositorio.ObtenerPorEmpresaAsync(request.EmpresaId, cancellationToken);

        if (credencial is null)
        {
            credencial = new CredencialAccesoEmpresa(
                request.EmpresaId, request.UrlAcceso, request.CampoEmpresa, request.Usuario, request.Contrasena, request.Notas);
            credencialRepositorio.Agregar(credencial);
        }
        else
        {
            credencial.Actualizar(request.UrlAcceso, request.CampoEmpresa, request.Usuario, request.Contrasena, request.Notas);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
