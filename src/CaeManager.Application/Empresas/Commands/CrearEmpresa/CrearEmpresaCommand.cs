using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
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
    IEmpresaRepository repositorio, IEmpresaClienteRepository empresaClienteRepositorio,
    IClientesQueryContext clientesContext, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearEmpresaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearEmpresaCommand request, CancellationToken cancellationToken)
    {
        if (await repositorio.ExisteConRazonSocialAsync(request.RazonSocial, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Empresa.RazonSocialDuplicada", "Ya existe una empresa con esta razón social."));

        if (!string.IsNullOrWhiteSpace(request.Cif) && await repositorio.ExisteConCifAsync(request.Cif, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Empresa.CifDuplicado", "Ya existe una empresa con este CIF."));

        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        var clienteIds = request.ClienteIds.Distinct().ToList();
        var clientesEncontrados = await clientesContext.Clientes
            .Where(c => clienteIds.Contains(c.Id))
            .CountAsync(cancellationToken);

        if (clientesEncontrados != clienteIds.Count)
            return Result.Fallo<Guid>(Error.Crear("Empresa.ClienteNoEncontrado", "Alguno de los clientes seleccionados no existe."));

        var empresa = new Empresa(request.RazonSocial, request.Cif, request.Cnae, request.ConvenioAplicable, request.EsActividadAnexoI);
        repositorio.Agregar(empresa);

        foreach (var clienteId in clienteIds)
            empresaClienteRepositorio.Agregar(new EmpresaCliente(empresa.Id, clienteId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(empresa.Id);
    }
}
