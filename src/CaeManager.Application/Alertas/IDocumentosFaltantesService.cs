using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Alertas;

/// <summary>
/// Un Trabajador con Asignación (real o hipotética) a un Centro que exige
/// (ver <see cref="Documentos.ResolucionTipoDocumentoCentro"/>) un
/// <c>TipoDocumento</c>, sin ningún <c>Documento</c> de ese tipo. Extraído de
/// <c>ObtenerAlertasQuery</c> (P1-15 de docs/business/MATURITY_REVIEW.md)
/// para que el mismo cálculo sirva tanto para "qué le falta a quien ya está
/// asignado" (Alertas) como para "qué le faltaría a quien estoy a punto de
/// asignar" (preflight de asignación en lote, Fase B) — misma regla de
/// negocio, dos preguntas.
/// </summary>
public record ParejaTrabajadorCentro(Guid TrabajadorId, string TrabajadorNombre, Guid CentroId, string CentroNombre);

public record DocumentoFaltanteDto(
    Guid TrabajadorId, string TrabajadorNombre, Guid CentroId, string CentroNombre, Guid TipoDocumentoId, string TipoDocumentoNombre);

public interface IDocumentosFaltantesService
{
    Task<IReadOnlyList<DocumentoFaltanteDto>> CalcularAsync(
        IReadOnlyList<ParejaTrabajadorCentro> parejas, CancellationToken cancellationToken);
}

public class DocumentosFaltantesService(ITiposDocumentoQueryContext tiposDocumentoContext, IDocumentosQueryContext documentosContext)
    : IDocumentosFaltantesService
{
    public async Task<IReadOnlyList<DocumentoFaltanteDto>> CalcularAsync(
        IReadOnlyList<ParejaTrabajadorCentro> parejas, CancellationToken cancellationToken)
    {
        if (parejas.Count == 0)
            return [];

        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, t.Nombre, t.EsObligatorio })
            .ToListAsync(cancellationToken);

        if (tiposCandidatos.Count == 0)
            return [];

        var tipoIdsCandidatos = tiposCandidatos.Select(t => t.Id).ToHashSet();
        var centroIds = parejas.Select(p => p.CentroId).Distinct().ToList();

        var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsCandidatos.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        var trabajadorIds = parejas.Select(p => p.TrabajadorId).Distinct().ToList();

        var documentosExistentes = await documentosContext.Documentos
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIdsCandidatos.Contains(d.TipoDocumentoId))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId })
            .ToListAsync(cancellationToken);

        var parejasConDocumento = documentosExistentes.Select(d => (d.TrabajadorId, d.TipoDocumentoId)).ToHashSet();

        var faltantes = new List<DocumentoFaltanteDto>();

        foreach (var pareja in parejas)
        {
            foreach (var tipo in tiposCandidatos)
            {
                if (!ResolucionTipoDocumentoCentro.Aplica(filasPorPar, tipo.Id, pareja.CentroId, tipo.EsObligatorio))
                    continue;

                if (parejasConDocumento.Contains((pareja.TrabajadorId, tipo.Id)))
                    continue;

                faltantes.Add(new DocumentoFaltanteDto(
                    pareja.TrabajadorId, pareja.TrabajadorNombre, pareja.CentroId, pareja.CentroNombre, tipo.Id, tipo.Nombre));
            }
        }

        return faltantes;
    }
}
