using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos;

/// <summary>
/// Qué documento representa a un titular (Trabajador, Empresa…) para un
/// TipoDocumento cuando tiene varios. El índice (propietario, TipoDocumentoId)
/// de Documento no es único y CrearDocumento no rechaza un segundo documento
/// del mismo tipo: la renovación típica deja el vencido y añade el nuevo. Un
/// <c>ToDictionary</c> por tipo sobre esos documentos lanza ArgumentException.
///
/// <para>
/// Orden de preferencia (el primero representa al tipo):
/// <list type="number">
/// <item>No vencido antes que vencido — vencido es <c>FechaVencimiento &lt; hoy</c>
/// según <see cref="CalculadoraEstadoDocumento"/>; los umbrales ámbar/rojo no
/// intervienen (Próximo y Urgente siguen vigentes).</item>
/// <item>Vigencia confirmada antes que <see cref="EstadoVigenciaDocumento.SinConfirmar"/>.</item>
/// <item>El que vence más tarde (sin fecha = no caduca, el primero).</item>
/// <item>El de emisión más reciente.</item>
/// </list>
/// Así, si hay uno vigente, gana el vigente más reciente; si solo hay vencidos,
/// gana el más reciente, y el estado que se muestra sigue siendo Vencido. Es el
/// mismo orden con el que el paquete de acreditación de la Visita elige qué
/// copia envía (<c>PaqueteDocumentalVisitaService.SeleccionarDocumentos</c>),
/// que además descarta los vencidos porque nunca los envía.
/// </para>
/// </summary>
public static class PreferenciaDocumentoPorTipo
{
    public static bool EstaVencido(EstadoVigenciaDocumento estadoVigencia, DateOnly? fechaVencimiento, DateOnly hoy) =>
        // Los umbrales no afectan a "Vencido"; 0/0 basta y evita leer ParametrosSistema.
        CalculadoraEstadoDocumento.Calcular(estadoVigencia, fechaVencimiento, hoy, 0, 0) == EstadoDocumento.Vencido;

    public static IOrderedEnumerable<T> Ordenar<T>(
        IEnumerable<T> documentos,
        Func<T, EstadoVigenciaDocumento> estadoVigencia,
        Func<T, DateOnly?> fechaVencimiento,
        Func<T, DateOnly> fechaEmision,
        DateOnly hoy) =>
        documentos
            .OrderBy(d => EstaVencido(estadoVigencia(d), fechaVencimiento(d), hoy))
            .ThenByDescending(d => estadoVigencia(d) != EstadoVigenciaDocumento.SinConfirmar)
            .ThenByDescending(d => fechaVencimiento(d) ?? DateOnly.MaxValue)
            .ThenByDescending(fechaEmision);

    /// <summary>Un documento por tipo: el preferido de cada grupo.</summary>
    public static Dictionary<TClave, T> UnoPorClave<T, TClave>(
        IEnumerable<T> documentos,
        Func<T, TClave> clave,
        Func<T, EstadoVigenciaDocumento> estadoVigencia,
        Func<T, DateOnly?> fechaVencimiento,
        Func<T, DateOnly> fechaEmision,
        DateOnly hoy) where TClave : notnull =>
        documentos
            .GroupBy(clave)
            .ToDictionary(g => g.Key, g => Ordenar(g, estadoVigencia, fechaVencimiento, fechaEmision, hoy).First());
}
