namespace CaeManager.Domain.Plantillas;

/// <summary>Cómo se rellena el archivo original al generar un documento — determina qué motor de estampado usa <c>IRellenadorPlantillaPdfService</c>.</summary>
public enum FormatoOrigenPlantilla
{
    /// <summary>PDF con campos de formulario (AcroForm) rellenables por nombre de campo.</summary>
    PdfConCampos,

    /// <summary>PDF sin campos — el valor se estampa por posición (x,y) sobre la página.</summary>
    PdfVisual,

    /// <summary>Solo <see cref="OrigenPlantilla.TalvegPropio"/> — HTML+CSS renderizado (ADR-007), no implementado en este MVP.</summary>
    HtmlCss
}
