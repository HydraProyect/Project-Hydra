using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Application.RelacionesEmpresariales;
using CaeManager.Domain.RelacionesEmpresariales;
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

public class EditarSubcontrataCommandHandler(
    IEmpresaRepository repositorio,
    IRelacionEmpresarialRepository relacionEmpresarialRepositorio,
    IEmpresasQueryContext empresasContext,
    IGuardDeCierreDeArista guardDeCierre,
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

        // F4.2c — una sola lectura clasificada da los dos ejes de "actuales"
        // (ver ContrapartesVigentes): EmpresaPropiaIds para el diff de
        // EmpresaIds, ClienteIds para el de ClienteIds. Una contraparte OPACA
        // (soft-deleted o no clasificable) no entra en ningún eje y por tanto
        // jamás se cierra por ausencia en el request — el invariante que
        // impide el borrado silencioso encontrado por la revisión adversarial
        // de F4.2b: las bajas se calculan sobre lo que el usuario pudo
        // desmarcar, no sobre lo que existe.
        var contrapartes = await relacionEmpresarialRepositorio.ObtenerContrapartesVigentesAsync(subcontrata.Id, cancellationToken);

        // EmpresaIds se resuelve ANTES que ClienteIds a propósito: el
        // candidato a enmarcadaEn de una relación Subcontrata→Cliente nueva
        // necesita el conjunto FINAL de Empresas vinculadas tras esta
        // edición — incluidas las que ya estaban antes, no solo las que se
        // añaden ahora — no el orden en que el código las escribe.
        var empresaIdsDeseados = request.EmpresaIds.Distinct().ToHashSet();
        var empresaIdsActuales = contrapartes.EmpresaPropiaIds.ToHashSet();

        var empresaIdsNuevos = empresaIdsDeseados.Except(empresaIdsActuales).ToList();
        if (await empresasContext.Empresas.Where(e => empresaIdsNuevos.Contains(e.Id)).CountAsync(cancellationToken) != empresaIdsNuevos.Count)
            return Result.Fallo(Error.Crear("Subcontrata.EmpresaNoEncontrada", "Alguna de las empresas seleccionadas no existe."));

        var clienteIdsDeseados = request.ClienteIds.Distinct().ToHashSet();
        var clienteIdsActuales = contrapartes.ClienteIds.ToHashSet();

        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        var clienteIdsNuevos = clienteIdsDeseados.Except(clienteIdsActuales).ToList();
        if (await empresasContext.Empresas.Where(e => clienteIdsNuevos.Contains(e.Id)).CountAsync(cancellationToken) != clienteIdsNuevos.Count)
            return Result.Fallo(Error.Crear("Subcontrata.ClienteNoEncontrado", "Alguno de los clientes seleccionados no existe."));

        // PD-1: se BLOQUEA, no se arrastra. Los dos guards van ANTES de
        // cualquier mutación: si el segundo bloqueara después de que el
        // primero ya hubiera cerrado aristas, el rechazo dependería de que
        // nadie llame a SaveChanges por el camino — una garantía que no está
        // escrita en ningún sitio y que el próximo que edite este handler no
        // tiene por qué conocer.
        //
        // Se evalúan sobre las BAJAS calculadas, nunca sobre el conjunto de
        // contrapartes: una contraparte opaca no origina baja y por tanto
        // tampoco bloqueo (invariante de F4.2c).
        var bajasDeCliente = clienteIdsActuales.Where(id => !clienteIdsDeseados.Contains(id)).ToList();

        // La segunda es la arista de PD-4 (subcontrata → contratista): la que
        // el guard de F5 § 5.4(a), formulado sobre Centros, no podía ver —
        // para ese par no existe ni puede existir una fila de Centro—. Era un
        // clic: abrir la ficha, desmarcar la contratista, guardar.
        var bajasDeEmpresa = empresaIdsActuales.Where(id => !empresaIdsDeseados.Contains(id)).ToList();

        foreach (var contraparteId in bajasDeCliente.Concat(bajasDeEmpresa))
            if (await guardDeCierre.TieneOperacionVivaAsync(subcontrata.Id, contraparteId, cancellationToken))
                return Result.Fallo(Error.Crear(
                    "Subcontrata.AristaConOperacionViva",
                    "No podemos desvincular esta relación: la subcontrata todavía tiene trabajadores asignados a centros de esa empresa. Retira primero las asignaciones."));

        // RazonSocial/Cif no tocan la arista; los diffs de EmpresaIds/
        // ClienteIds sí. Una baja NO re-resuelve retroactivamente el
        // enmarcadaEn de relaciones Subcontrata→Cliente ya existentes — eso
        // sería editar una relación in situ, y el modelo es append-only;
        // solo las relaciones NUEVAS de esta edición usan el enmarcadaEn
        // resuelto aquí.
        foreach (var clienteId in bajasDeCliente)
            await relacionEmpresarialRepositorio.CerrarVigenteAsync(subcontrata.Id, clienteId, ahora, cancellationToken);

        foreach (var clienteId in clienteIdsNuevos)
        {
            var enmarcadaEnId = await relacionEmpresarialRepositorio.ObtenerCandidatoUnicoParaEnmarcarAsync(
                empresaIdsDeseados, clienteId, cancellationToken);
            await relacionEmpresarialRepositorio.AgregarSiNoVigenteAsync(
                subcontrata.Id, clienteId, ahora, enmarcadaEnId, cancellationToken);
        }

        foreach (var empresaId in bajasDeEmpresa)
            await relacionEmpresarialRepositorio.CerrarVigenteAsync(subcontrata.Id, empresaId, ahora, cancellationToken);

        foreach (var empresaId in empresaIdsNuevos)
            await relacionEmpresarialRepositorio.AgregarSiNoVigenteAsync(
                subcontrata.Id, empresaId, ahora, cancellationToken: cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
