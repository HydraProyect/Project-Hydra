using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Commands.CrearEmpresa;

/// <summary>
/// <see cref="IComandoDeAprovisionamiento" /> (PD-A3): forma parte del alta
/// planificada de contenido CAE en un tenant durante su aprovisionamiento
/// inicial — ver <c>AutorizacionEscrituraBehavior</c>.
/// </summary>
public record CrearEmpresaCommand(
    string RazonSocial,
    string? Cif,
    IReadOnlyList<Guid> ClienteIds,
    string? Cnae = null,
    string? ConvenioAplicable = null,
    bool EsActividadAnexoI = false) : ICommand<Guid>, IComandoDeAprovisionamiento;

public class CrearEmpresaCommandValidator : AbstractValidator<CrearEmpresaCommand>
{
    public CrearEmpresaCommandValidator()
    {
        RuleFor(c => c.RazonSocial)
            .NotEmpty().WithMessage("La razón social es obligatoria.")
            .MaximumLength(Empresa.LongitudMaximaRazonSocial)
            .WithMessage($"La razón social no puede superar {Empresa.LongitudMaximaRazonSocial} caracteres.");

        // Obligatoria en el alta para MVP-1 (Escenario 2, tecnico/docs/MULTITENANCY.md
        // § 2 — el tenant ES la Empresa contratista): sin ella no se puede emitir un
        // F-22 válido, va en cabecera y en la cláusula RGPD. Un autónomo la cumple
        // con su DNI o su NIE — no se le exige un CIF que no tiene.
        RuleFor(c => c.Cif).NotEmpty().WithMessage("La identificación fiscal es obligatoria.");

        RuleFor(c => c.Cif)
            .Must(ValidadorIdentificacion.EsIdentificacionFiscalValida)
            .WithMessage(Empresa.MensajeIdentificacionFiscalInvalida)
            .When(c => !string.IsNullOrWhiteSpace(c.Cif));

        RuleFor(c => c.Cnae).MaximumLength(Empresa.LongitudMaximaCnae);
        RuleFor(c => c.ConvenioAplicable).MaximumLength(Empresa.LongitudMaximaConvenioAplicable);
    }

}

public class CrearEmpresaCommandHandler(
    IEmpresaRepository repositorio,
    IRelacionEmpresarialRepository relacionEmpresarialRepositorio,
    IEmpresasQueryContext empresasContext, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearEmpresaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearEmpresaCommand request, CancellationToken cancellationToken)
    {
        if (await repositorio.ExisteConRazonSocialAsync(request.RazonSocial, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Empresa.RazonSocialDuplicada", "Ya existe una empresa con esta razón social."));

        if (!string.IsNullOrWhiteSpace(request.Cif) && await repositorio.ExisteConCifAsync(request.Cif, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Empresa.CifDuplicado", "Ya existe una empresa con este CIF."));

        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        // EmpresaCliente.ClienteId ya apunta a Empresas (F3): el "cliente" que se
        // vincula aquí es un registro de Empresas, no de la tabla Clientes congelada.
        var clienteIds = request.ClienteIds.Distinct().ToList();
        var clientesEncontrados = await empresasContext.Empresas
            .Where(e => clienteIds.Contains(e.Id))
            .CountAsync(cancellationToken);

        if (clientesEncontrados != clienteIds.Count)
            return Result.Fallo<Guid>(Error.Crear("Empresa.ClienteNoEncontrado", "Alguno de los clientes seleccionados no existe."));

        var empresa = new Empresa(request.RazonSocial, request.Cif, request.Cnae, request.ConvenioAplicable, request.EsActividadAnexoI);
        repositorio.Agregar(empresa);

        // F4.2c — RelacionEmpresarial es la ÚNICA fuente de escritura (R6
        // aceptada 2026-08-27; la tabla legacy EmpresaCliente ya no recibe
        // altas). Solo el alta de ClienteIds toca la arista: RazonSocial/Cif/
        // Cnae/ConvenioAplicable/EsActividadAnexoI son identidad de Empresa,
        // no de la relación.
        var ahora = DateTime.UtcNow;
        foreach (var clienteId in clienteIds)
            await relacionEmpresarialRepositorio.AgregarSiNoVigenteAsync(
                empresa.Id, clienteId, ahora, cancellationToken: cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(empresa.Id);
    }
}
