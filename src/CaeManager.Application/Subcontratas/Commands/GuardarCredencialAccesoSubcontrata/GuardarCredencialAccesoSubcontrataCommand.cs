using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Subcontratas.Commands.GuardarCredencialAccesoSubcontrata;

/// <summary>
/// Alta o edición en un solo paso (upsert): una Subcontrata tiene como mucho
/// un juego de credenciales — mismo patrón que GuardarCredencialAccesoEmpresaCommand.
///
/// <c>Contrasena</c> vacío o null en una edición **conserva la almacenada**
/// (DEC-62) — el formulario ya no la carga al abrirse
/// (<c>ObtenerCredencialAccesoSubcontrataSinContrasenaQuery</c>), así que un
/// campo vacío no puede significar "bórrala" sin borrarla en silencio al
/// guardar cualquier otro cambio (p. ej. la razón social). En un alta
/// (todavía no hay fila) no aplica: ahí vacío/null simplemente significa que
/// no se ha puesto contraseña.
/// </summary>
public record GuardarCredencialAccesoSubcontrataCommand(
    Guid SubcontrataId, string? UrlAcceso, string? CampoEmpresa, string? Usuario, string? Contrasena, string? Notas = null) : ICommand;

public class GuardarCredencialAccesoSubcontrataCommandValidator : AbstractValidator<GuardarCredencialAccesoSubcontrataCommand>
{
    public GuardarCredencialAccesoSubcontrataCommandValidator()
    {
        RuleFor(c => c.SubcontrataId).NotEmpty();
        RuleFor(c => c.UrlAcceso).MaximumLength(CredencialAccesoSubcontrata.LongitudMaximaUrlAcceso);
        RuleFor(c => c.CampoEmpresa).MaximumLength(CredencialAccesoSubcontrata.LongitudMaximaCampoEmpresa);
        RuleFor(c => c.Usuario).MaximumLength(CredencialAccesoSubcontrata.LongitudMaximaUsuario);
        RuleFor(c => c.Contrasena).MaximumLength(CredencialAccesoSubcontrata.LongitudMaximaContrasena);
        RuleFor(c => c.Notas).MaximumLength(CredencialAccesoSubcontrata.LongitudMaximaNotas);
    }
}

public class GuardarCredencialAccesoSubcontrataCommandHandler(
    IEmpresaRepository subcontrataRepositorio,
    ICredencialAccesoSubcontrataRepository credencialRepositorio,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarCredencialAccesoSubcontrataCommand, Result>
{
    public async Task<Result> Handle(GuardarCredencialAccesoSubcontrataCommand request, CancellationToken cancellationToken)
    {
        var subcontrata = await subcontrataRepositorio.ObtenerPorIdAsync(request.SubcontrataId, cancellationToken);
        if (subcontrata is null || !await alcanceDatos.SubcontrataVisibleAsync(subcontrata.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Subcontrata.NoEncontrada", "No encontramos esta subcontrata."));

        var credencial = await credencialRepositorio.ObtenerPorSubcontrataAsync(request.SubcontrataId, cancellationToken);

        if (credencial is null)
        {
            credencial = new CredencialAccesoSubcontrata(
                request.SubcontrataId, request.UrlAcceso, request.CampoEmpresa, request.Usuario, request.Contrasena, request.Notas);
            credencialRepositorio.Agregar(credencial);
        }
        else
        {
            // DEC-62: vacío/null conserva la contraseña ya almacenada.
            var contrasena = string.IsNullOrEmpty(request.Contrasena) ? credencial.Contrasena : request.Contrasena;
            credencial.Actualizar(request.UrlAcceso, request.CampoEmpresa, request.Usuario, contrasena, request.Notas);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
