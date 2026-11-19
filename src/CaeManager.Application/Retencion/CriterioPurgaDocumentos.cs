using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Retencion;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Retencion;

/// <summary>Un Documento con su plazo cumplido y el plazo efectivo, en años, con el que se decidió.</summary>
public sealed record CandidatoPurgaDocumento(Guid DocumentoId, int Anios);

/// <summary>
/// Qué Documentos han cumplido su plazo de conservación (decisión del 2026-11-18), resuelto en SQL:
/// <list type="bullet">
/// <item>plazo efectivo = el del tipo de documento ?? el del Tenant propietario;</item>
/// <item>inicio = <see cref="CriterioInicioRetencion.EventoMasTemprano"/> entre el evento propio del Documento
/// (<see cref="CalculadoraRetencionDocumento.EventoDelDocumento"/>: vencimiento ?? emisión) y su sustitución
/// (<see cref="Documento.SustituidoEnUtc"/>, por su día de negocio en Europe/Madrid, <see cref="DiaDeNegocio"/>);</item>
/// <item>purgable si inicio ≤ hoy − plazo.</item>
/// </list>
/// Como el plazo solo puede valer de 1 a 5 años (<see cref="PlazoRetencionDocumentos"/>), cada valor lleva su corte
/// precalculado y la consulta elige el suyo: no hay aritmética de fechas sobre columnas que dependa del traductor, y
/// un plazo fuera de rango que se colara en la base no casa con ninguna rama y no purga nada (falla cerrado).
/// <c>CriterioPurgaDocumentosTests</c> comprueba que coincide con <see cref="CalculadoraRetencionDocumento.PuedePurgarse"/>.
/// <para>
/// Lo usan la detección, para proponer, y la ejecución, sobre la lista propuesta (R4): lo que la detección no propuso
/// no se purga, y lo propuesto que hoy ya no cumple el plazo (vigencia corregida, plazo alargado) se conserva.
/// </para>
/// <para>
/// No acota por Tenant propietario ni por anonimizado: lo hace quien llama, sobre las dos fuentes que pasa.
/// </para>
/// </summary>
public static class CriterioPurgaDocumentos
{
    /// <summary>El plazo del Tenant propietario; sin fila de configuración, el de por defecto (R2).</summary>
    public static async Task<int> PlazoDelTenantAsync(
        IQueryable<ParametroSistema> parametros, Guid tenantId, CancellationToken cancellationToken) =>
        await parametros
            .Where(p => p.TenantId == tenantId)
            .Select(p => (int?)p.AniosRetencionDocumentos)
            .FirstOrDefaultAsync(cancellationToken) ?? PlazoRetencionDocumentos.PorDefecto;

    public static IQueryable<CandidatoPurgaDocumento> Candidatos(
        IQueryable<Documento> documentos, IQueryable<TipoDocumento> tipos, int aniosTenant, DateOnly hoy) =>
        Cumplidos(documentos, tipos, aniosTenant, hoy).Select(x => new CandidatoPurgaDocumento(x.Documento.Id, x.Anios));

    /// <summary>Los mismos Documentos que <see cref="Candidatos"/>, como entidades, para componer en la ejecución.</summary>
    public static IQueryable<Documento> Purgables(
        IQueryable<Documento> documentos, IQueryable<TipoDocumento> tipos, int aniosTenant, DateOnly hoy) =>
        Cumplidos(documentos, tipos, aniosTenant, hoy).Select(x => x.Documento);

    private static IQueryable<Fila> Cumplidos(
        IQueryable<Documento> documentos, IQueryable<TipoDocumento> tipos, int aniosTenant, DateOnly hoy)
    {
        var (c1, s1) = Corte(hoy, 1);
        var (c2, s2) = Corte(hoy, 2);
        var (c3, s3) = Corte(hoy, 3);
        var (c4, s4) = Corte(hoy, 4);
        var (c5, s5) = Corte(hoy, 5);

        return documentos
            .Join(tipos, d => d.TipoDocumentoId, t => t.Id, (d, t) => new Fila
            {
                Documento = d,
                Evento = d.FechaVencimiento ?? d.FechaEmision,
                Sustitucion = d.SustituidoEnUtc,
                Anios = t.AniosRetencionDocumentos ?? aniosTenant,
            })
            .Where(x =>
                (x.Anios == 1 && (x.Evento <= c1 || x.Sustitucion < s1)) ||
                (x.Anios == 2 && (x.Evento <= c2 || x.Sustitucion < s2)) ||
                (x.Anios == 3 && (x.Evento <= c3 || x.Sustitucion < s3)) ||
                (x.Anios == 4 && (x.Evento <= c4 || x.Sustitucion < s4)) ||
                (x.Anios == 5 && (x.Evento <= c5 || x.Sustitucion < s5)));
    }

    /// <summary>
    /// El corte de un plazo y su equivalente para la sustitución: una sustitución cuyo día de negocio (Europe/Madrid)
    /// es ≤ corte es la que ocurrió antes de la medianoche de Madrid del día siguiente al corte, expresada en UTC. Ese
    /// límite cae antes que la medianoche UTC, así que, frente a contar por la fecha UTC, el desvío es hacia conservar.
    /// </summary>
    private static (DateOnly Corte, DateTime SustituidoAntesDeUtc) Corte(DateOnly hoy, int anios)
    {
        var corte = hoy.AddYears(-anios);
        return (corte, DiaDeNegocio.InicioEnUtc(corte.AddDays(1)));
    }

    private sealed class Fila
    {
        public required Documento Documento { get; init; }
        public required DateOnly Evento { get; init; }
        /// <summary>Cuándo se sustituyó el Documento (<see cref="Documento.SustituidoEnUtc"/>), o null.</summary>
        public required DateTime? Sustitucion { get; init; }
        public required int Anios { get; init; }
    }
}
