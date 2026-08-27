using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Un nombre alternativo por el que se puede encontrar un
/// <see cref="TipoDocumento"/> — sigla, alias histórico o forma corta
/// (p. ej. "TC2" para "Relación Nominal de Trabajadores", "CIF" para
/// "Tarjeta de identificación fiscal"). Precondición de la limpieza de
/// nombres del catálogo (taxonomía documental CAE §2bis, patrón D): sin este
/// campo, la única forma de que alguien encuentre "TC2" es que esté escrito
/// dentro del nombre, que es justo el problema que este campo resuelve.
///
/// Entidad hija de <see cref="TipoDocumento"/> (colección de solo lectura
/// sobre campo privado, mismo patrón que <c>ContactoAgendaTipoDocumento</c>).
/// </summary>
public class TipoDocumentoAlias : EntidadConTenant
{
    public const int LongitudMaximaTexto = 100;

    public Guid TipoDocumentoId { get; private set; }
    public string Texto { get; private set; } = string.Empty;

    private TipoDocumentoAlias()
    {
    }

    public TipoDocumentoAlias(Guid tipoDocumentoId, string texto)
    {
        if (tipoDocumentoId == Guid.Empty)
            throw new ArgumentException("El alias debe pertenecer a un tipo de documento.", nameof(tipoDocumentoId));

        if (string.IsNullOrWhiteSpace(texto))
            throw new ArgumentException("El texto del alias no puede estar vacío.", nameof(texto));

        var normalizado = texto.Trim();
        if (normalizado.Length > LongitudMaximaTexto)
            throw new ArgumentException($"El alias no puede superar {LongitudMaximaTexto} caracteres.", nameof(texto));

        TipoDocumentoId = tipoDocumentoId;
        Texto = normalizado;
    }
}
