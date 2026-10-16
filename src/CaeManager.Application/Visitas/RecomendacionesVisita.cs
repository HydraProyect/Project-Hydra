using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Visitas;

public enum TipoRecomendacionVisita
{
    /// <summary>La Empresa o un Trabajador tiene documentos exigidos por el Centro que faltan o están vencidos.</summary>
    DocumentacionPendiente,

    /// <summary>Un documento hoy en regla vence antes del día de entrada: el día de la Visita ya no lo estará.</summary>
    VenceAntesDeLaEntrada,

    /// <summary>Un documento en regla el día de entrada vence antes del último día de la Visita.</summary>
    VenceDuranteLaVisita,
}

/// <summary>
/// Una recomendación de Visita 360. <paramref name="Sujeto"/> es el nombre de la Empresa o del Trabajador tal como lo
/// trae la documentación de la Visita; <paramref name="TiposDocumento"/>, los tipos de documento afectados; y
/// <paramref name="Fecha"/>, la de vencimiento cuando la recomendación habla de un vencimiento.
/// </summary>
public sealed record RecomendacionVisita(
    TipoRecomendacionVisita Tipo, string Sujeto, IReadOnlyList<string> TiposDocumento, DateOnly? Fecha = null);

/// <summary>
/// Recomendaciones de Visita 360 (decisión de producto, 2026-10-16): solo se dan si salen de datos que ya existen,
/// nunca texto inventado. Todas se derivan de <see cref="DocumentacionVisitaDto"/>, que ya trae solo lo que el
/// Centro de la Visita exige, y de las fechas de la Visita:
/// <list type="bullet">
/// <item>documentación pendiente (Faltante o Vencido, el mismo criterio de incidencia que la pestaña de
/// comprobación) de la Empresa o de cada Trabajador, una por sujeto;</item>
/// <item>documentos hoy en regla cuya fecha de vencimiento cae antes del último día de la Visita, uno por
/// documento, distinguiendo si vence antes del día de entrada o durante la Visita.</item>
/// </list>
/// Sin datos de los que salgan, la lista queda vacía y la página no pinta la sección. Una Visita cancelada o ya
/// terminada no recomienda nada: no hay entrada que preparar.
/// </summary>
public static class RecomendacionesVisita
{
    public static IReadOnlyList<RecomendacionVisita> Calcular(
        DocumentacionVisitaDto documentacion, string empresaNombre,
        DateOnly fechaInicio, DateOnly fechaFin, DateOnly hoy, bool estaCancelada)
    {
        if (estaCancelada || fechaFin < hoy) return [];

        var sujetos = new List<(string Sujeto, SeccionDocumentacionDto Seccion)> { (empresaNombre, documentacion.Empresa) };
        sujetos.AddRange(documentacion.Trabajadores.Select(t => (t.NombreCompleto, t.Documentacion)));

        var pendientes = new List<RecomendacionVisita>();
        var vencimientos = new List<RecomendacionVisita>();
        foreach (var (sujeto, seccion) in sujetos)
        {
            var incidencias = seccion.Documentos.Where(EsIncidencia).Select(d => d.TipoDocumentoNombre).Distinct().ToList();
            if (incidencias.Count > 0)
                pendientes.Add(new RecomendacionVisita(TipoRecomendacionVisita.DocumentacionPendiente, sujeto, incidencias));

            foreach (var documento in seccion.Documentos)
            {
                if (EsIncidencia(documento))
                    continue;
                if (documento.FechaVencimiento is not { } vence)
                    continue;
                if (vence >= fechaFin)
                    continue;

                var tipo = vence < fechaInicio ? TipoRecomendacionVisita.VenceAntesDeLaEntrada : TipoRecomendacionVisita.VenceDuranteLaVisita;
                vencimientos.Add(new RecomendacionVisita(tipo, sujeto, [documento.TipoDocumentoNombre], vence));
            }
        }

        return [.. pendientes, .. vencimientos.OrderBy(r => r.Fecha)];
    }

    private static bool EsIncidencia(DocumentoVisitaItemDto documento) =>
        documento.Estado is EstadoDocumento.Vencido or EstadoDocumento.Faltante;
}
