using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Empresas.Commands.EditarFichaEmpresa;

public record EditarFichaEmpresaCommand(Guid Id, Guid Version,
    string? CanalFirmaDocumentos,
    bool? SelloCustodiadoPorOperadorCae,
    string? FirmanteDocumentos,
    string? ModalidadOrganizacionPreventiva,
    string? OrganizacionSpa,
    string? ContactoSpaNombre,
    string? ContactoSpaTelefono,
    string? ContactoSpaCorreo,
    string? Mutua,
    string? ContactoFormacionNombre,
    string? ContactoFormacionTelefono,
    string? ContactoFormacionCorreo,
    string? TecnicoPrlNombre,
    string? TecnicoPrlTelefono,
    string? TecnicoPrlCorreo) : ICommand;

public class EditarFichaEmpresaCommandValidator : AbstractValidator<EditarFichaEmpresaCommand>
{
    public EditarFichaEmpresaCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Version).NotEmpty();
        RuleFor(c => c.CanalFirmaDocumentos).MaximumLength(Empresa.LongitudMaximaCampoFicha);
        RuleFor(c => c.FirmanteDocumentos).MaximumLength(Empresa.LongitudMaximaCampoFicha);
        RuleFor(c => c.ModalidadOrganizacionPreventiva).MaximumLength(Empresa.LongitudMaximaCampoFicha);
        RuleFor(c => c.OrganizacionSpa).MaximumLength(Empresa.LongitudMaximaCampoFicha);
        RuleFor(c => c.ContactoSpaNombre).MaximumLength(Empresa.LongitudMaximaCampoFicha);
        RuleFor(c => c.ContactoSpaTelefono).MaximumLength(Empresa.LongitudMaximaTelefonoFicha);
        RuleFor(c => c.ContactoSpaCorreo).MaximumLength(Empresa.LongitudMaximaCorreoFicha);
        RuleFor(c => c.ContactoSpaCorreo).EmailAddress().WithMessage("Introduce un correo electrónico válido.").When(c => !string.IsNullOrWhiteSpace(c.ContactoSpaCorreo));
        RuleFor(c => c.Mutua).MaximumLength(Empresa.LongitudMaximaCampoFicha);
        RuleFor(c => c.ContactoFormacionNombre).MaximumLength(Empresa.LongitudMaximaCampoFicha);
        RuleFor(c => c.ContactoFormacionTelefono).MaximumLength(Empresa.LongitudMaximaTelefonoFicha);
        RuleFor(c => c.ContactoFormacionCorreo).MaximumLength(Empresa.LongitudMaximaCorreoFicha);
        RuleFor(c => c.ContactoFormacionCorreo).EmailAddress().WithMessage("Introduce un correo electrónico válido.").When(c => !string.IsNullOrWhiteSpace(c.ContactoFormacionCorreo));
        RuleFor(c => c.TecnicoPrlNombre).MaximumLength(Empresa.LongitudMaximaCampoFicha);
        RuleFor(c => c.TecnicoPrlTelefono).MaximumLength(Empresa.LongitudMaximaTelefonoFicha);
        RuleFor(c => c.TecnicoPrlCorreo).MaximumLength(Empresa.LongitudMaximaCorreoFicha);
        RuleFor(c => c.TecnicoPrlCorreo).EmailAddress().WithMessage("Introduce un correo electrónico válido.").When(c => !string.IsNullOrWhiteSpace(c.TecnicoPrlCorreo));
        RuleFor(c => c.CanalFirmaDocumentos).Must(c => string.IsNullOrWhiteSpace(c) || c.Trim() is Empresa.CanalFirmaDocuSign or Empresa.CanalFirmaPorCorreo)
            .WithMessage("El canal de firma debe ser DocuSign o Por correo.");
    }
}

public class EditarFichaEmpresaCommandHandler(IEmpresaRepository repositorio,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarFichaEmpresaCommand, Result>
{
    public async Task<Result> Handle(EditarFichaEmpresaCommand request, CancellationToken cancellationToken)
    {
        var empresa = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (empresa is null || !await alcanceDatos.EmpresaParaGestionVisibleAsync(request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa."));
        if (ConcurrenciaOptimista.Verificar(empresa, request.Version, "esta empresa") is { } conflicto)
            return Result.Fallo(conflicto);
        empresa.ActualizarFicha(
            request.CanalFirmaDocumentos,
            request.SelloCustodiadoPorOperadorCae,
            request.FirmanteDocumentos,
            request.ModalidadOrganizacionPreventiva,
            request.OrganizacionSpa,
            request.ContactoSpaNombre,
            request.ContactoSpaTelefono,
            request.ContactoSpaCorreo,
            request.Mutua,
            request.ContactoFormacionNombre,
            request.ContactoFormacionTelefono,
            request.ContactoFormacionCorreo,
            request.TecnicoPrlNombre,
            request.TecnicoPrlTelefono,
            request.TecnicoPrlCorreo);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
