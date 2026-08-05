using CaeManager.Domain.Common;

namespace CaeManager.Domain.RequisitosDocumentales;

/// <summary>
/// Exigencia adicional de un Centro más allá de la documentación base común
/// a todos los centros. Se mantiene como texto libre a propósito: el Excel
/// original muestra que estos requisitos son heterogéneos y ad-hoc por
/// cliente — forzar una relación estructurada con TipoDocumento sería
/// prematuro (ver DATABASE.md).
///
/// El adjunto (<see cref="ArchivoUrl"/>) es deliberadamente más simple que
/// Documento: un identificador de almacenamiento opaco sin
/// TipoDocumento/vencimiento/alertas, porque lo que se adjunta aquí es el
/// propio formulario en blanco que hay que rellenar (Word/PDF, convertido y
/// unificado igual que en Documentos — ver ConversorArchivosPdf en
/// CaeManager.Web), no un justificante con caducidad. Forzarlo a la
/// estructura de Documento habría exigido AmbitoAplicacion.Centro y un
/// TipoDocumentoCentro que nada más necesita todavía.
///
/// No dispara alertas: la detección de "documento faltante" (ver
/// ObtenerAlertasQuery) se apoya en TipoDocumento.EsObligatorio +
/// TipoDocumentoCentro, no en este agregado — un RequisitoDocumental sin
/// cumplir no aparece hoy en /alertas. Sí participa en
/// CalculadoraEstadoCentro: uno con BloqueaAcceso y sin Cumplido fuerza el
/// Centro a EstadoCentro.Bloqueado, porque a diferencia de Documento (donde
/// el propio ArchivoUrl es el justificante) aquí ArchivoUrl es solo la
/// plantilla en blanco — <see cref="Cumplido"/> es el único campo que
/// certifica que el cliente devolvió el formulario relleno, y lo marca el
/// Gestor a mano.
/// </summary>
public class RequisitoDocumental : EntidadBase
{
    public const int LongitudMaximaDescripcion = 1000;
    public const int LongitudMaximaPeriodicidad = 300;
    public const int LongitudMaximaNotas = 1000;
    public const int LongitudMaximaArchivoUrl = 500;
    public const int LongitudMaximaNombreArchivo = 260;

    public Guid CentroId { get; private set; }
    public string Descripcion { get; private set; } = string.Empty;
    public string? PeriodicidadEspecial { get; private set; }
    public bool BloqueaAcceso { get; private set; }
    public string? Notas { get; private set; }
    public string? ArchivoUrl { get; private set; }
    public string? NombreArchivoOriginal { get; private set; }
    public bool Cumplido { get; private set; }
    public DateOnly? FechaCumplimiento { get; private set; }

    private RequisitoDocumental()
    {
    }

    public RequisitoDocumental(
        Guid centroId,
        string descripcion,
        string? periodicidadEspecial,
        bool bloqueaAcceso,
        string? notas = null,
        string? archivoUrl = null,
        string? nombreArchivoOriginal = null)
    {
        if (centroId == Guid.Empty)
            throw new ArgumentException("El requisito debe pertenecer a un centro.", nameof(centroId));

        CentroId = centroId;
        EstablecerDescripcion(descripcion);
        PeriodicidadEspecial = periodicidadEspecial;
        BloqueaAcceso = bloqueaAcceso;
        Notas = notas;
        ArchivoUrl = archivoUrl;
        NombreArchivoOriginal = nombreArchivoOriginal;
    }

    public void Actualizar(
        string descripcion, string? periodicidadEspecial, bool bloqueaAcceso, string? notas,
        string? archivoUrl, string? nombreArchivoOriginal)
    {
        EstablecerDescripcion(descripcion);
        PeriodicidadEspecial = periodicidadEspecial;
        BloqueaAcceso = bloqueaAcceso;
        Notas = notas;
        ArchivoUrl = archivoUrl;
        NombreArchivoOriginal = nombreArchivoOriginal;
    }

    public void MarcarCumplido(DateOnly fecha)
    {
        Cumplido = true;
        FechaCumplimiento = fecha;
    }

    public void DesmarcarCumplido()
    {
        Cumplido = false;
        FechaCumplimiento = null;
    }

    private void EstablecerDescripcion(string descripcion)
    {
        if (string.IsNullOrWhiteSpace(descripcion))
            throw new ArgumentException("La descripción del requisito es obligatoria.", nameof(descripcion));

        var normalizada = descripcion.Trim();

        if (normalizada.Length > LongitudMaximaDescripcion)
            throw new ArgumentException(
                $"La descripción no puede superar {LongitudMaximaDescripcion} caracteres.", nameof(descripcion));

        Descripcion = normalizada;
    }
}
