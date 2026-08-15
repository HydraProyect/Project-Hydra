using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plantillas;

/// <summary>
/// Nivel B de ADR-010 § 2.4 — conocimiento de detección, global y compartido
/// entre tenants (a diferencia de <see cref="PlantillaElemento"/>, Nivel A,
/// que es memoria de una plantilla concreta de un tenant concreto). Asocia
/// una etiqueta de campo normalizada (p. ej. "cif" detectado en un AcroForm
/// o en el texto de un PDF visual) con la <see cref="FuenteDatoPlantilla"/>
/// que probablemente resuelve — solo una sugerencia para la detección
/// inicial, nunca escrita automáticamente desde la corrección de un gestor
/// (eso contaminaría la detección de otros tenants).
///
/// Deliberadamente extiende <see cref="Entity"/>, no <see cref="EntidadBase"/>:
/// catálogo global del producto, mismo criterio que
/// <c>ProveedorPlataformaCae</c>. Poblarla o versionarla administrativamente
/// queda fuera del MVP (ADR-010 § 4) — hoy solo se siembra por migración.
/// </summary>
public class ConocimientoDeteccionCampo : Entity
{
    public const int LongitudMaximaEtiquetaNormalizada = 200;

    /// <summary>Minúsculas, sin acentos, espacios colapsados — ver <c>NormalizadorEtiquetaCampo</c>.</summary>
    public string EtiquetaNormalizada { get; private set; } = string.Empty;

    public FuenteDatoPlantilla FuenteDatoCandidata { get; private set; }

    /// <summary>Mayor gana cuando varias filas coinciden con la misma etiqueta. No es un peso de aprendizaje (ver ADR-010 § 2.4).</summary>
    public int Prioridad { get; private set; }

    private ConocimientoDeteccionCampo()
    {
    }

    public ConocimientoDeteccionCampo(string etiquetaNormalizada, FuenteDatoPlantilla fuenteDatoCandidata, int prioridad)
    {
        if (string.IsNullOrWhiteSpace(etiquetaNormalizada))
            throw new ArgumentException("La etiqueta normalizada es obligatoria.", nameof(etiquetaNormalizada));
        if (etiquetaNormalizada.Length > LongitudMaximaEtiquetaNormalizada)
            throw new ArgumentException($"La etiqueta no puede superar {LongitudMaximaEtiquetaNormalizada} caracteres.", nameof(etiquetaNormalizada));
        if (fuenteDatoCandidata is FuenteDatoPlantilla.Manual or FuenteDatoPlantilla.Constante)
            throw new ArgumentException("Manual y Constante no son fuentes que la detección pueda sugerir.", nameof(fuenteDatoCandidata));

        EtiquetaNormalizada = etiquetaNormalizada;
        FuenteDatoCandidata = fuenteDatoCandidata;
        Prioridad = prioridad;
    }
}
