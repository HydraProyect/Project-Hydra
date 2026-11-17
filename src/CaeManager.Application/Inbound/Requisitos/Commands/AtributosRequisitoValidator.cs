using CaeManager.Application.Inbound.CatalogoBase;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.CatalogoBase;
using CaeManager.Domain.Inbound.Requisitos;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.Requisitos.Commands;

/// <summary>
/// Forma de <see cref="AtributosRequisito"/> antes de llegar al dominio. Las reglas que dependen del tipo de documento,
/// de la plantilla o del requisito de base (R-6, D-15, nivel) las aplica <see cref="RequisitoPlantilla"/>.
/// </summary>
public class AtributosRequisitoValidator : AbstractValidator<AtributosRequisito>
{
    public AtributosRequisitoValidator()
    {
        RuleFor(a => a.Origen).IsInEnum();
        RuleFor(a => a.ModoVigencia).IsInEnum();
        // shortcut: SePideA queda sin leerse; retirar la columna en un incremento posterior (decisión 2026-11-17)
        RuleFor(a => a.SePideA).IsInEnum();
        RuleFor(a => a.AmbitoDocumento).IsInEnum();
        RuleFor(a => a.VigenciaMeses).InclusiveBetween(1, AtributosRequisito.VigenciaMaximaMeses).When(a => a.VigenciaMeses is not null);
        RuleFor(a => a.ToleranciaDias).InclusiveBetween(0, AtributosRequisito.ToleranciaMaximaDias).When(a => a.ToleranciaDias is not null);
        RuleFor(a => a.Criterio).MaximumLength(AtributosRequisito.LongitudMaximaCriterio);
        RuleFor(a => a.Equivalencias).NotNull();
        RuleFor(a => a.Equivalencias.Count).LessThanOrEqualTo(AtributosRequisito.MaximoEquivalencias).When(a => a.Equivalencias is not null);
        RuleForEach(a => a.Equivalencias).MaximumLength(AtributosRequisito.LongitudMaximaEquivalencia);
    }
}

/// <summary>Carga lo que el dominio necesita para validar un requisito: el tipo de documento y el requisito de base.</summary>
internal static class CargaRequisito
{
    public static async Task<(TipoDocumento? Tipo, RequisitoBase? Base, Error? Error)> CargarAsync(
        ITipoDocumentoRepository tipos, ICatalogoBaseQueryContext catalogo, Guid tipoDocumentoId, Guid? requisitoBaseId,
        CancellationToken cancellationToken)
    {
        var tipo = await tipos.ObtenerPorIdAsync(tipoDocumentoId, cancellationToken);
        if (tipo is null)
            return (null, null, PlantillaRequisitosErrores.TipoDocumentoNoEncontrado);

        if (requisitoBaseId is not { } baseId)
            return (tipo, null, null);

        var requisitoBase = await catalogo.CatalogoRequisitosBase.FirstOrDefaultAsync(r => r.Id == baseId, cancellationToken);
        return requisitoBase is null
            ? (null, null, PlantillaRequisitosErrores.RequisitoBaseNoEncontrado)
            : (tipo, requisitoBase, null);
    }
}
