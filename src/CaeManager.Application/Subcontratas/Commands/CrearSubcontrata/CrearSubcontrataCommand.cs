using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.RelacionesEmpresariales;
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
    IEmpresaRepository repositorio,
    ISubcontrataClienteRepository subcontrataClienteRepositorio,
    ISubcontrataEmpresaRepository subcontrataEmpresaRepositorio,
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

        // Doble escritura F4 (transitoria — ver SincronizacionRelacionEmpresarial):
        // RazonSocial/Cif no tocan RelacionEmpresarial. EmpresaIds y ClienteIds
        // sí — ambos son de primer nivel (proveedora=subcontrata), salvo que
        // ClienteId coincida con el EnmarcadaEnId resuelto por debajo.
        //
        // El candidato de enmarcadaEn se resuelve contra `empresaIds` EN
        // MEMORIA (no contra la BD): los vínculos SubcontrataEmpresa de esta
        // Subcontrata nueva todavía no existen en RelacionEmpresarial en este
        // punto de la transacción, así que el repositorio no podría
        // encontrarlos si se le pidiera resolverlos por su propia Id.
        var ahora = DateTime.UtcNow;

        foreach (var empresaId in empresaIds)
        {
            subcontrataEmpresaRepositorio.Agregar(new SubcontrataEmpresa(subcontrata.Id, empresaId));
            await SincronizacionRelacionEmpresarial.SincronizarAltaAsync(
                relacionEmpresarialRepositorio, subcontrata.Id, empresaId, ahora, cancellationToken: cancellationToken);
        }

        foreach (var clienteId in clienteIds)
        {
            subcontrataClienteRepositorio.Agregar(new SubcontrataCliente(subcontrata.Id, clienteId));
            var enmarcadaEnId = await relacionEmpresarialRepositorio.ObtenerCandidatoUnicoParaEnmarcarAsync(
                empresaIds, clienteId, cancellationToken);
            await SincronizacionRelacionEmpresarial.SincronizarAltaAsync(
                relacionEmpresarialRepositorio, subcontrata.Id, clienteId, ahora, enmarcadaEnId, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(subcontrata.Id);
    }
}
