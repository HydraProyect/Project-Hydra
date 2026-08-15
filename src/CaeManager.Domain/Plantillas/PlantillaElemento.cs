using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plantillas;

/// <summary>
/// Una cosa colocable sobre una página del PDF de una
/// <see cref="PlantillaDocumentoVersion"/> — dato o firma, con su posición
/// para el editor visual (ADR-010 § 2.3, § 2.8). Solo tiene sentido para
/// <see cref="OrigenPlantilla.Externa"/>: una plantilla
/// <see cref="OrigenPlantilla.TalvegPropio"/> no tiene filas aquí, su
/// mapeo vive en código (§ 2.9).
/// </summary>
public class PlantillaElemento : EntidadConTenant
{
    public const int LongitudMaximaEtiquetaVisible = 200;
    public const int LongitudMaximaValorConstante = 500;
    public const int LongitudMaximaFormato = 50;

    public Guid PlantillaDocumentoVersionId { get; private set; }
    public TipoElementoPlantilla Tipo { get; private set; }

    public int Pagina { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Ancho { get; private set; }
    public double Alto { get; private set; }

    public string EtiquetaVisible { get; private set; } = string.Empty;

    /// <summary>Informado cuando <see cref="Tipo"/> es un dato (Texto/Fecha/Checkbox/Constante) — null para <see cref="TipoElementoPlantilla.Firma"/>.</summary>
    public FuenteDatoPlantilla? FuenteDato { get; private set; }

    /// <summary>Obligatorio cuando <see cref="FuenteDato"/> es <see cref="FuenteDatoPlantilla.Constante"/>.</summary>
    public string? ValorConstante { get; private set; }

    /// <summary>Máscara de formato opcional (p.ej. "dd/MM/yyyy" para Fecha).</summary>
    public string? Formato { get; private set; }

    public bool Obligatorio { get; private set; }

    /// <summary>Informado solo cuando <see cref="Tipo"/> es <see cref="TipoElementoPlantilla.Firma"/>.</summary>
    public RolFirmantePlantilla? RolFirmante { get; private set; }

    private PlantillaElemento()
    {
    }

    public PlantillaElemento(
        Guid plantillaDocumentoVersionId,
        TipoElementoPlantilla tipo,
        int pagina,
        double x,
        double y,
        double ancho,
        double alto,
        string etiquetaVisible,
        FuenteDatoPlantilla? fuenteDato = null,
        string? valorConstante = null,
        string? formato = null,
        bool obligatorio = false,
        RolFirmantePlantilla? rolFirmante = null)
    {
        if (plantillaDocumentoVersionId == Guid.Empty)
            throw new ArgumentException("El elemento debe pertenecer a una versión de plantilla.", nameof(plantillaDocumentoVersionId));
        if (pagina < 1)
            throw new ArgumentException("La página debe ser 1 o mayor.", nameof(pagina));
        if (ancho <= 0 || alto <= 0)
            throw new ArgumentException("El ancho y el alto del elemento deben ser mayores que cero.", nameof(ancho));

        EstablecerEtiquetaVisible(etiquetaVisible);

        if (tipo == TipoElementoPlantilla.Firma && rolFirmante is null)
            throw new ArgumentException("Un elemento de firma debe indicar quién firma.", nameof(rolFirmante));
        if (tipo != TipoElementoPlantilla.Firma && rolFirmante is not null)
            throw new ArgumentException("Solo un elemento de firma puede tener rol de firmante.", nameof(rolFirmante));
        if (fuenteDato == FuenteDatoPlantilla.Constante && string.IsNullOrWhiteSpace(valorConstante))
            throw new ArgumentException("Un elemento con fuente Constante debe tener un valor.", nameof(valorConstante));

        PlantillaDocumentoVersionId = plantillaDocumentoVersionId;
        Tipo = tipo;
        Pagina = pagina;
        X = x;
        Y = y;
        Ancho = ancho;
        Alto = alto;
        FuenteDato = fuenteDato;
        ValorConstante = Truncar(valorConstante, LongitudMaximaValorConstante);
        Formato = Truncar(formato, LongitudMaximaFormato);
        Obligatorio = obligatorio;
        RolFirmante = rolFirmante;
    }

    /// <summary>El editor visual mueve/redimensiona sin recrear el elemento — conserva Id y el resto de configuración.</summary>
    public void Reposicionar(int pagina, double x, double y, double ancho, double alto)
    {
        if (pagina < 1)
            throw new ArgumentException("La página debe ser 1 o mayor.", nameof(pagina));
        if (ancho <= 0 || alto <= 0)
            throw new ArgumentException("El ancho y el alto del elemento deben ser mayores que cero.", nameof(ancho));

        Pagina = pagina;
        X = x;
        Y = y;
        Ancho = ancho;
        Alto = alto;
    }

    private void EstablecerEtiquetaVisible(string etiquetaVisible)
    {
        if (string.IsNullOrWhiteSpace(etiquetaVisible))
            throw new ArgumentException("La etiqueta visible del elemento es obligatoria.", nameof(etiquetaVisible));

        var normalizada = etiquetaVisible.Trim();
        if (normalizada.Length > LongitudMaximaEtiquetaVisible)
            throw new ArgumentException($"La etiqueta no puede superar {LongitudMaximaEtiquetaVisible} caracteres.", nameof(etiquetaVisible));

        EtiquetaVisible = normalizada;
    }

    private static string? Truncar(string? valor, int longitudMaxima) =>
        valor?.Length > longitudMaxima ? valor[..longitudMaxima] : valor;
}
