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

namespace CaeManager.Application.Subcontratas.Commands.EditarSubcontrata;

public record EditarSubcontrataCommand(
    Guid Id, string RazonSocial, string? Cif, IReadOnlyList<Guid> ClienteIds, IReadOnlyList<Guid> EmpresaIds,
    Guid Version = default) : ICommand;

public class EditarSubcontrataCommandValidator : AbstractValidator<EditarSubcontrataCommand>
{
    public EditarSubcontrataCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

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

public class EditarSubcontrataCommandHandler(
    IEmpresaRepository repositorio,
    ISubcontrataClienteRepository subcontrataClienteRepositorio,
    ISubcontrataEmpresaRepository subcontrataEmpresaRepositorio,
    IRelacionEmpresarialRepository relacionEmpresarialRepositorio,
    IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EditarSubcontrataCommand, Result>
{
    public async Task<Result> Handle(EditarSubcontrataCommand request, CancellationToken cancellationToken)
    {
        var subcontrata = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (subcontrata is null || !await alcanceDatos.SubcontrataVisibleAsync(subcontrata.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Subcontrata.NoEncontrada", "No encontramos esta subcontrata."));

        if (ConcurrenciaOptimista.Verificar(subcontrata, request.Version, "esta subcontrata") is { } conflicto)
            return Result.Fallo(conflicto);

        if (await repositorio.ExisteConRazonSocialAsync(request.RazonSocial, request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Subcontrata.RazonSocialDuplicada", "Ya existe una subcontrata con esta razón social."));

        if (!string.IsNullOrWhiteSpace(request.Cif) && await repositorio.ExisteConCifAsync(request.Cif, request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Subcontrata.CifDuplicado", "Ya existe una subcontrata con este CIF."));

        subcontrata.ActualizarComoSubcontrata(request.RazonSocial, request.Cif);

        var ahora = DateTime.UtcNow;

        // EmpresaIds se resuelve ANTES que ClienteIds a propósito: el
        // candidato a enmarcadaEn de una relación Subcontrata→Cliente nueva
        // necesita el conjunto FINAL de Empresas vinculadas tras esta
        // edición — incluidas las que ya estaban antes, no solo las que se
        // añaden ahora — no el orden en que el código las escribe.
        var empresasActuales = await subcontrataEmpresaRepositorio.ObtenerPorSubcontrataAsync(subcontrata.Id, cancellationToken);
        var empresaIdsDeseados = request.EmpresaIds.Distinct().ToHashSet();
        var empresaIdsActuales = empresasActuales.Select(se => se.EmpresaId).ToHashSet();

        var empresaIdsNuevos = empresaIdsDeseados.Except(empresaIdsActuales).ToList();
        if (await empresasContext.Empresas.Where(e => empresaIdsNuevos.Contains(e.Id)).CountAsync(cancellationToken) != empresaIdsNuevos.Count)
            return Result.Fallo(Error.Crear("Subcontrata.EmpresaNoEncontrada", "Alguna de las empresas seleccionadas no existe."));

        var clientesActuales = await subcontrataClienteRepositorio.ObtenerPorSubcontrataAsync(subcontrata.Id, cancellationToken);
        var clienteIdsDeseados = request.ClienteIds.Distinct().ToHashSet();
        var clienteIdsActuales = clientesActuales.Select(sc => sc.ClienteId).ToHashSet();

        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        var clienteIdsNuevos = clienteIdsDeseados.Except(clienteIdsActuales).ToList();
        if (await empresasContext.Empresas.Where(e => clienteIdsNuevos.Contains(e.Id)).CountAsync(cancellationToken) != clienteIdsNuevos.Count)
            return Result.Fallo(Error.Crear("Subcontrata.ClienteNoEncontrado", "Alguno de los clientes seleccionados no existe."));

        // Doble escritura F4 (transitoria — ver SincronizacionRelacionEmpresarial):
        // RazonSocial/Cif no tocan RelacionEmpresarial. Los diffs de
        // EmpresaIds/ClienteIds sí. Una baja de EmpresaIds/ClienteIds NO
        // re-resuelve retroactivamente el enmarcadaEn de relaciones
        // Subcontrata→Cliente ya existentes — eso sería editar una relación
        // in situ, y el modelo es append-only; solo las relaciones NUEVAS de
        // este mismo alta usan el enmarcadaEn resuelto aquí.
        foreach (var sc in clientesActuales.Where(sc => !clienteIdsDeseados.Contains(sc.ClienteId)))
        {
            subcontrataClienteRepositorio.Eliminar(sc);
            await SincronizacionRelacionEmpresarial.SincronizarBajaAsync(
                relacionEmpresarialRepositorio, subcontrata.Id, sc.ClienteId, ahora, cancellationToken);
        }

        foreach (var clienteId in clienteIdsNuevos)
        {
            subcontrataClienteRepositorio.Agregar(new SubcontrataCliente(subcontrata.Id, clienteId));
            var enmarcadaEnId = await relacionEmpresarialRepositorio.ObtenerCandidatoUnicoParaEnmarcarAsync(
                empresaIdsDeseados, clienteId, cancellationToken);
            await SincronizacionRelacionEmpresarial.SincronizarAltaAsync(
                relacionEmpresarialRepositorio, subcontrata.Id, clienteId, ahora, enmarcadaEnId, cancellationToken);
        }

        foreach (var se in empresasActuales.Where(se => !empresaIdsDeseados.Contains(se.EmpresaId)))
        {
            subcontrataEmpresaRepositorio.Eliminar(se);
            await SincronizacionRelacionEmpresarial.SincronizarBajaAsync(
                relacionEmpresarialRepositorio, subcontrata.Id, se.EmpresaId, ahora, cancellationToken);
        }

        foreach (var empresaId in empresaIdsNuevos)
        {
            subcontrataEmpresaRepositorio.Agregar(new SubcontrataEmpresa(subcontrata.Id, empresaId));
            await SincronizacionRelacionEmpresarial.SincronizarAltaAsync(
                relacionEmpresarialRepositorio, subcontrata.Id, empresaId, ahora, cancellationToken: cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
