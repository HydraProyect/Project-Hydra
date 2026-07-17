using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Catálogo maestro de documentos PRL exigibles (apto médico, EPIS,
/// formación, reciclajes, etc.). Corresponde 1:1 a la hoja "Parametros" del
/// Excel original. Es configuración del sistema, editable solo por
/// Administrador.
/// </summary>
public class TipoDocumento : Entity
{
    public const int LongitudMaximaNombre = 150;
    public const int LongitudMaximaNotas = 500;
    public const int LongitudMaximaDescripcion = 1000;
    public const int LongitudMaximaCriteriosValidacion = 1000;
    public const int LongitudMaximaSeSolicitaA = 300;
    public const int LongitudMaximaObservaciones = 1000;

    public string Nombre { get; private set; } = string.Empty;
    public int? VigenciaMeses { get; private set; }
    public bool AplicaVencimientoAutomatico { get; private set; }
    public string? Notas { get; private set; }
    public int Orden { get; private set; }
    public string? Descripcion { get; private set; }
    public string? CriteriosValidacion { get; private set; }
    public string? SeSolicitaA { get; private set; }
    public string? Observaciones { get; private set; }

    private TipoDocumento()
    {
    }

    public TipoDocumento(
        string nombre,
        int? vigenciaMeses,
        bool aplicaVencimientoAutomatico,
        int orden,
        string? notas = null,
        string? descripcion = null,
        string? criteriosValidacion = null,
        string? seSolicitaA = null,
        string? observaciones = null)
    {
        EstablecerNombre(nombre);
        EstablecerVigencia(vigenciaMeses, aplicaVencimientoAutomatico);
        Orden = orden;
        Notas = notas;
        EstablecerGlosario(descripcion, criteriosValidacion, seSolicitaA, observaciones);
    }

    public void Actualizar(
        string nombre,
        int? vigenciaMeses,
        bool aplicaVencimientoAutomatico,
        int orden,
        string? notas,
        string? descripcion,
        string? criteriosValidacion,
        string? seSolicitaA,
        string? observaciones)
    {
        EstablecerNombre(nombre);
        EstablecerVigencia(vigenciaMeses, aplicaVencimientoAutomatico);
        Orden = orden;
        Notas = notas;
        EstablecerGlosario(descripcion, criteriosValidacion, seSolicitaA, observaciones);
    }

    private void EstablecerGlosario(string? descripcion, string? criteriosValidacion, string? seSolicitaA, string? observaciones)
    {
        if (descripcion?.Length > LongitudMaximaDescripcion)
            throw new ArgumentException($"La descripción no puede superar {LongitudMaximaDescripcion} caracteres.", nameof(descripcion));

        if (criteriosValidacion?.Length > LongitudMaximaCriteriosValidacion)
            throw new ArgumentException($"Los criterios de validación no pueden superar {LongitudMaximaCriteriosValidacion} caracteres.", nameof(criteriosValidacion));

        if (seSolicitaA?.Length > LongitudMaximaSeSolicitaA)
            throw new ArgumentException($"\"Se solicita a\" no puede superar {LongitudMaximaSeSolicitaA} caracteres.", nameof(seSolicitaA));

        if (observaciones?.Length > LongitudMaximaObservaciones)
            throw new ArgumentException($"Las observaciones no pueden superar {LongitudMaximaObservaciones} caracteres.", nameof(observaciones));

        Descripcion = descripcion;
        CriteriosValidacion = criteriosValidacion;
        SeSolicitaA = seSolicitaA;
        Observaciones = observaciones;
    }

    private void EstablecerVigencia(int? vigenciaMeses, bool aplicaVencimientoAutomatico)
    {
        if (aplicaVencimientoAutomatico && (vigenciaMeses is null || vigenciaMeses <= 0))
            throw new ArgumentException(
                "Un tipo de documento con vencimiento automático debe tener una vigencia en meses mayor que cero.",
                nameof(vigenciaMeses));

        VigenciaMeses = vigenciaMeses;
        AplicaVencimientoAutomatico = aplicaVencimientoAutomatico;
    }

    private void EstablecerNombre(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El nombre del tipo de documento es obligatorio.", nameof(nombre));

        var normalizado = nombre.Trim();

        if (normalizado.Length > LongitudMaximaNombre)
            throw new ArgumentException(
                $"El nombre no puede superar {LongitudMaximaNombre} caracteres.", nameof(nombre));

        Nombre = normalizado;
    }
}
