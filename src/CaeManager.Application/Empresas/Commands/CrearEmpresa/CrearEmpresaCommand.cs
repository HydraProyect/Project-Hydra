using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Commands.CrearEmpresa;

public record CrearEmpresaCommand(
    string RazonSocial,
    string? Cif,
    IReadOnlyList<Guid> ClienteIds,
    string? Cnae = null,
    string? ConvenioAplicable = null,
    bool EsActividadAnexoI = false) : ICommand<Guid>;

public class CrearEmpresaCommandValidator : AbstractValidator<CrearEmpresaCommand>
{
    public CrearEmpresaCommandValidator()
    {
        RuleFor(c => c.RazonSocial)
            .NotEmpty().WithMessage("La razón social es obligatoria.")
            .MaximumLength(Empresa.LongitudMaximaRazonSocial)
            .WithMessage($"La razón social no puede superar {Empresa.LongitudMaximaRazonSocial} caracteres.");

        // Obligatorio en el alta para MVP-1 (Escenario 2, tecnico/docs/MULTITENANCY.md
        // § 2 — el tenant ES la Empresa contratista): sin CIF no se puede emitir un
        // F-22 válido, va en cabecera y en la cláusula RGPD.
        RuleFor(c => c.Cif).NotEmpty().WithMessage("El CIF es obligatorio.");

        RuleFor(c => c.Cif)
            .Must(EsCifValido).WithMessage("El CIF no es válido.")
            .When(c => !string.IsNullOrWhiteSpace(c.Cif));

        RuleFor(c => c.Cnae).MaximumLength(Empresa.LongitudMaximaCnae);
        RuleFor(c => c.ConvenioAplicable).MaximumLength(Empresa.LongitudMaximaConvenioAplicable);
    }

    private static bool EsCifValido(string? cif)
    {
        var resultado = ValidadorIdentificacion.Analizar(cif!);
        return resultado.Tipo == TipoIdentificacion.NifEmpresa && resultado.EsValido;
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
