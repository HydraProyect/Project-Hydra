using CaeManager.Domain.Documentos;
using CaeManager.Domain.Retencion;

namespace CaeManager.Application.Retencion;

/// <summary>Un Documento con su plazo cumplido y el plazo efectivo, en años, con el que se decidió.</summary>
public sealed record CandidatoPurgaDocumento(Guid DocumentoId, int Anios);

/// <summary>
/// Qué Documentos han cumplido su plazo de conservación (decisión del 2026-11-18), resuelto en SQL:
/// <list type="bullet">
/// <item>plazo efectivo = el del tipo de documento ?? el del Tenant propietario;</item>
/// <item>inicio = <see cref="CriterioInicioRetencion.EventoMasTemprano"/> entre el evento propio del Documento
/// (<see cref="CalculadoraRetencionDocumento.EventoDelDocumento"/>: vencimiento ?? emisión) y su sustitución
/// (<see cref="Documento.SustituidoEnUtc"/>, por su fecha UTC);</item>
/// <item>purgable si inicio ≤ hoy − plazo.</item>
/// </list>
/// Como el plazo solo puede valer de 1 a 5 años (<see cref="PlazoRetencionDocumentos"/>), cada valor lleva su corte
/// precalculado y la consulta elige el suyo: no hay aritmética de fechas sobre columnas que dependa del traductor, y
/// un plazo fuera de rango que se colara en la base no casa con ninguna rama y no purga nada (falla cerrado).
/// <c>CriterioPurgaDocumentosTests</c> comprueba que coincide con <see cref="CalculadoraRetencionDocumento.PuedePurgarse"/>.
/// <para>
/// No acota por Tenant propietario ni por anonimizado: lo hace quien llama, sobre las dos fuentes que pasa.
/// </para>
/// </summary>
public static class CriterioPurgaDocumentos
{
    public static IQueryable<CandidatoPurgaDocumento> Candidatos(
        IQueryable<Documento> documentos, IQueryable<TipoDocumento> tipos, int aniosTenant, DateOnly hoy)
    {
        var (c1, s1) = Corte(hoy, 1);
        var (c2, s2) = Corte(hoy, 2);
        var (c3, s3) = Corte(hoy, 3);
        var (c4, s4) = Corte(hoy, 4);
        var (c5, s5) = Corte(hoy, 5);

        return documentos
            .Join(tipos, d => d.TipoDocumentoId, t => t.Id, (d, t) => new
            {
                d.Id,
                Evento = d.FechaVencimiento ?? d.FechaEmision,
                d.SustituidoEnUtc,
                Anios = t.AniosRetencionDocumentos ?? aniosTenant,
            })
            .Where(x =>
                (x.Anios == 1 && (x.Evento <= c1 || x.SustituidoEnUtc < s1)) ||
                (x.Anios == 2 && (x.Evento <= c2 || x.SustituidoEnUtc < s2)) ||
                (x.Anios == 3 && (x.Evento <= c3 || x.SustituidoEnUtc < s3)) ||
                (x.Anios == 4 && (x.Evento <= c4 || x.SustituidoEnUtc < s4)) ||
                (x.Anios == 5 && (x.Evento <= c5 || x.SustituidoEnUtc < s5)))
            .Select(x => new CandidatoPurgaDocumento(x.Id, x.Anios));
    }

    /// <summary>
    /// El corte de un plazo y su equivalente para la sustitución: una sustitución de fecha UTC ≤ corte es la que
    /// ocurrió antes del inicio del día siguiente al corte, en UTC.
    /// </summary>
    private static (DateOnly Corte, DateTime SustituidoAntesDeUtc) Corte(DateOnly hoy, int anios)
    {
        var corte = hoy.AddYears(-anios);
        return (corte, corte.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }
}
