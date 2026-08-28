using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
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
            .MaximumLength(Empresa.LongitudMaximaRazonSocial)
            .WithMessage($"La razón social no puede superar {Empresa.LongitudMaximaRazonSocial} caracteres.");

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
    IEmpresaRepository repositorio,
    IRelacionEmpresarialRepository relacionEmpresarialRepositorio,
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

        var subcontrata = Empresa.CrearComoSubcontrata(request.RazonSocial, request.Cif, NivelServicioSubcontrata.Gestionada.ToString());
        repositorio.Agregar(subcontrata);

        // F4.2c — RelacionEmpresarial es la única fuente de escritura (R6
        // aceptada 2026-08-27). RazonSocial/Cif no tocan la arista. EmpresaIds
        // y ClienteIds sí — ambos de primer nivel (proveedora=subcontrata),
        // salvo el EnmarcadaEnId resuelto por debajo.
        //
        // El candidato de enmarcadaEn se resuelve contra `empresaIds` EN
        // MEMORIA (no contra los vínculos de esta Subcontrata en la BD): sus
        // aristas Subcontrata→Empresa todavía no están persistidas en este
        // punto de la transacción. La resolución cruza contra las relaciones
        // Empresa propia→Cliente, que sí existen desde antes.
        var ahora = DateTime.UtcNow;

        foreach (var empresaId in empresaIds)
            await relacionEmpresarialRepositorio.AgregarSiNoVigenteAsync(
                subcontrata.Id, empresaId, ahora, cancellationToken: cancellationToken);

        foreach (var clienteId in clienteIds)
        {
            var enmarcadaEnId = await relacionEmpresarialRepositorio.ObtenerCandidatoUnicoParaEnmarcarAsync(
                empresaIds, clienteId, cancellationToken);
            await relacionEmpresarialRepositorio.AgregarSiNoVigenteAsync(
                subcontrata.Id, clienteId, ahora, enmarcadaEnId, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(subcontrata.Id);
    }
}
