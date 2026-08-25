using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Subcontratas;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;

public record CrearSubcontrataCommand(
    string RazonSocial, string? Cif, IReadOnlyList<Guid> ClienteIds, IReadOnlyList<Guid> EmpresaIds) : ICommand<Guid>;

public class CrearSubcontrataCommandValidator : AbstractValidator<CrearSubcontrataCommand>
{
    public CrearSubcontrataCommandValidator()
    {
        RuleFor(c => c.RazonSocial)
            .NotEmpty().WithMessage("La razón social es obligatoria.")
            .MaximumLength(Subcontrata.LongitudMaximaRazonSocial)
            .WithMessage($"La razón social no puede superar {Subcontrata.LongitudMaximaRazonSocial} caracteres.");

        RuleFor(c => c.Cif)
            .Must(EsCifValido).WithMessage("El CIF no es válido.")
            .When(c => !string.IsNullOrWhiteSpace(c.Cif));
    }

    private static bool EsCifValido(string? cif)
    {
        var resultado = ValidadorIdentificacion.Analizar(cif!);
        return resultado.Tipo == TipoIdentificacion.NifEmpresa && resultado.EsValido;
    }
}

public class CrearSubcontrataCommandHandler(
    ISubcontrataRepository repositorio,
    ISubcontrataClienteRepository subcontrataClienteRepositorio,
    ISubcontrataEmpresaRepository subcontrataEmpresaRepositorio,
    IEmpresasQueryContext empresasContext,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearSubcontrataCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearSubcontrataCommand request, CancellationToken cancellationToken)
    {
        if (await repositorio.ExisteConRazonSocialAsync(request.RazonSocial, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Subcontrata.RazonSocialDuplicada", "Ya existe una subcontrata con esta razón social."));

        if (!string.IsNullOrWhiteSpace(request.Cif) && await repositorio.ExisteConCifAsync(request.Cif, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Subcontrata.CifDuplicado", "Ya existe una subcontrata con este CIF."));

        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        var clienteIds = request.ClienteIds.Distinct().ToList();
        var empresaIds = request.EmpresaIds.Distinct().ToList();

        if (await empresasContext.Empresas.Where(e => clienteIds.Contains(e.Id)).CountAsync(cancellationToken) != clienteIds.Count)
            return Result.Fallo<Guid>(Error.Crear("Subcontrata.ClienteNoEncontrado", "Alguno de los clientes seleccionados no existe."));

        if (await empresasContext.Empresas.Where(e => empresaIds.Contains(e.Id)).CountAsync(cancellationToken) != empresaIds.Count)
            return Result.Fallo<Guid>(Error.Crear("Subcontrata.EmpresaNoEncontrada", "Alguna de las empresas seleccionadas no existe."));

        var subcontrata = new Subcontrata(request.RazonSocial, request.Cif);
        repositorio.Agregar(subcontrata);

        foreach (var clienteId in clienteIds)
            subcontrataClienteRepositorio.Agregar(new SubcontrataCliente(subcontrata.Id, clienteId));

        foreach (var empresaId in empresaIds)
            subcontrataEmpresaRepositorio.Agregar(new SubcontrataEmpresa(subcontrata.Id, empresaId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(subcontrata.Id);
    }
}
