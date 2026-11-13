using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// Las Plataformas CAE de un Documento tal como las enseña el listado (columna «Plataformas CAE» del agrupado por tipo) y como las
/// exporta el Excel del listado (D-20): la misma regla en los dos sitios, para que el archivo no diga otra cosa que la pantalla.
/// Solo nombre y estado de cada acreditación: nunca el motivo del rechazo, la dirección del portal ni la anotación del Centro 360.
/// </summary>
public static class PlataformasCaeDelDocumento
{
    /// <summary>
    /// Una línea por Plataforma CAE (no por Centro): un Documento destinado a varios Centros de la misma plataforma tiene una
    /// acreditación por Centro, y repetirlas haría crecer la fila sin límite. De cada plataforma se queda el estado más grave, y
    /// salen primero las más graves (rechazada, pendiente de subir, subida, aceptada) y luego por nombre.
    /// </summary>
    public static IReadOnlyList<AcreditacionResumenDto> Resumir(IReadOnlyList<AcreditacionResumenDto> acreditaciones) =>
        acreditaciones
            .GroupBy(a => a.NombrePlataforma, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.OrderBy(a => Prioridad(a.Estado)).First())
            .OrderBy(a => Prioridad(a.Estado))
            .ThenBy(a => a.NombrePlataforma, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>
    /// Celda «Plataformas CAE» del Excel (D-20): «Nalanda: Pendiente de subir; Dokify: Validada», con los rótulos de D-22 (el estado
    /// de la acreditación en femenino y con mayúscula inicial). Vacía si el Documento no está acreditado en ninguna.
    /// </summary>
    public static string TextoParaExcel(IReadOnlyList<AcreditacionResumenDto> acreditaciones) =>
        string.Join("; ", Resumir(acreditaciones).Select(a => $"{a.NombrePlataforma}: {EstadoAcreditacionUi.TextoCapitalizado(a.Estado)}"));

    private static int Prioridad(EstadoAcreditacion estado) => estado switch
    {
        EstadoAcreditacion.Rechazada => 0,
        EstadoAcreditacion.PendienteDeSubir => 1,
        EstadoAcreditacion.Subida => 2,
        EstadoAcreditacion.Aceptada => 3,
        _ => 4
    };
}
