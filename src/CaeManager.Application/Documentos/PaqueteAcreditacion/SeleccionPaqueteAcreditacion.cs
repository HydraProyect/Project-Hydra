using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos.PaqueteAcreditacion;

public enum MotivoExclusionPaquete
{
    Vencido = 1,
    NoEsLaVersionMasReciente = 2,
    SinArchivo = 3
}

/// <param name="TitularId">Trabajador o Empresa a quien pertenece: la selección es «una por tipo» DENTRO de cada titular.</param>
public record DocumentoCandidatoPaquete(
    Guid Id,
    Guid TitularId,
    Guid TipoDocumentoId,
    EstadoVigenciaDocumento EstadoVigencia,
    DateOnly? FechaVencimiento,
    DateOnly FechaEmision,
    DateTime CreadoEnUtc,
    string? ArchivoUrl);

/// <param name="Ordinal">1 = único para su titular, tipo y emisión; N ≥ 2 → sufijo <c>_vN</c>.</param>
public record DecisionPaquete(
    DocumentoCandidatoPaquete Documento,
    bool Incluido,
    MotivoExclusionPaquete? Motivo,
    int Ordinal,
    bool VigenciaSinConfirmar);

/// <summary>
/// Regla del paquete de acreditación (decisión del propietario, 2026-10-01): se envían solo
/// documentos vigentes y, por titular y tipo, solo la versión de FECHA DE EMISIÓN más reciente
/// (aunque otra tenga más vigencia). Nunca los vencidos. Sin vigencia NO es vencido: «no caduca»
/// y «sin confirmar» entran (una formación de 2012 que no caduca sirve en 2026); el «sin confirmar»
/// va marcado en el índice. Sin desempate por vencimiento: si coinciden titular, tipo y emisión,
/// entran todos y llevan <c>_v2</c>, <c>_v3</c>… por orden estable de creación y, después, de Id.
///
/// <para>
/// Es una regla distinta de <see cref="PreferenciaDocumentoPorTipo"/> (que prefiere la mayor
/// vigencia y usa el paquete de la Visita): aquella NO se toca. Un documento sin archivo no se
/// puede enviar: se excluye antes de elegir la versión más reciente, así que no esconde a otro
/// con archivo.
/// </para>
/// </summary>
public static class SeleccionPaqueteAcreditacion
{
    public static IReadOnlyList<DecisionPaquete> Seleccionar(IEnumerable<DocumentoCandidatoPaquete> documentos, DateOnly hoy)
    {
        var decisiones = new List<DecisionPaquete>();

        foreach (var grupo in documentos.GroupBy(d => (d.TitularId, d.TipoDocumentoId)))
        {
            var vigentes = new List<DocumentoCandidatoPaquete>();
            foreach (var d in grupo)
            {
                if (PreferenciaDocumentoPorTipo.EstaVencido(d.EstadoVigencia, d.FechaVencimiento, hoy))
                    decisiones.Add(new DecisionPaquete(d, false, MotivoExclusionPaquete.Vencido, 0, false));
                else if (string.IsNullOrWhiteSpace(d.ArchivoUrl))
                    decisiones.Add(new DecisionPaquete(d, false, MotivoExclusionPaquete.SinArchivo, 0, false));
                else
                    vigentes.Add(d);
            }

            if (vigentes.Count == 0)
                continue;

            var emisionMasReciente = vigentes.Max(d => d.FechaEmision);
            var ordinal = 0;
            foreach (var d in vigentes.OrderByDescending(d => d.FechaEmision == emisionMasReciente)
                         .ThenBy(d => d.CreadoEnUtc).ThenBy(d => d.Id))
            {
                if (d.FechaEmision == emisionMasReciente)
                    decisiones.Add(new DecisionPaquete(
                        d, true, null, ++ordinal, d.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar));
                else
                    decisiones.Add(new DecisionPaquete(d, false, MotivoExclusionPaquete.NoEsLaVersionMasReciente, 0, false));
            }
        }

        return decisiones;
    }
}
