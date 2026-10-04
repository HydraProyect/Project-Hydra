using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos;

/// <summary>
/// El <b>documento efectivo</b>: de los documentos operativos de un titular y un Tipo, el que representa a esa unidad
/// en el estado, el cumplimiento y el paquete (diseño del documento efectivo, 2026-10-03, § 2.4). Absorbe a la
/// antigua <c>PreferenciaDocumentoPorTipo</c> y a <c>PreferenciaCopiaDelPaquete</c>: es el <b>único</b> sitio donde se
/// escribe el orden de elección (<c>ReglasDeNegocioSinCopiasTests</c> lo vigila).
///
/// <para>
/// Quien llama pasa solo candidatos <b>operativos</b> (<see cref="DocumentoOperativo"/>): un documento sustituido es
/// historial y nunca compite. Con una sola unidad viva por (titular, Tipo, destinatario) el orden solo decide entre un
/// documento general y uno nominativo; con duplicados aún sin resolver (D6) decide cuál manda, siempre igual.
/// </para>
///
/// <para>
/// Orden (el primero es el efectivo):
/// <list type="number">
/// <item><b>Válido hoy</b> antes que vencido — vencido es <c>FechaVencimiento &lt; hoy</c> según
/// <see cref="CalculadoraEstadoDocumento"/>; los umbrales ámbar/rojo no intervienen (Próximo y Urgente valen).
/// «Sin confirmar» y «No caduca» no están vencidos.</item>
/// <item><b>Nominativo antes que general</b> (D3): el emitido a nombre del Cliente empresarial del requisito gana al
/// general. Inerte hasta la PR 6 del diseño (el destinatario aún no existe en el modelo): los llamadores no pasan
/// <c>esNominativo</c> y ningún documento lo es.</item>
/// <item><b>Emisión más reciente</b> (<c>FechaEmision</c>), aunque otro venza más tarde (decisión del propietario del
/// 2026-10-01 para el paquete; «vence más tarde» ya no interviene).</item>
/// <item><b>Vigencia confirmada</b> antes que «Sin confirmar».</item>
/// <item>Dado de alta más tarde (<c>CreadoEnUtc</c>) y, por último, el menor <c>Id</c>: solo para que la elección no
/// dependa del orden en que devuelva las filas la base.</item>
/// </list>
/// </para>
///
/// <para>
/// Selección y estado no se mezclan: este orden elige el documento; el estado que se muestra (umbrales, tolerancia y
/// periodicidad del Centro) lo calcula aparte quien lo pinta. Si solo hay vencidos, el efectivo es el más reciente y su
/// estado sigue siendo Vencido. El paquete de la Visita toma de aquí al efectivo y solo lo envía si <see cref="ValidoHoy"/>.
/// </para>
/// </summary>
public static class DocumentoEfectivo
{
    /// <summary>
    /// ¿Vale hoy? No está vencido: sin fecha (No caduca o Sin confirmar) o con fecha &gt;= hoy. Es la definición única de
    /// «vencido» para elegir y para decidir qué viaja en el paquete; no incluye tolerancia ni periodicidad del Centro.
    /// </summary>
    public static bool ValidoHoy(EstadoVigenciaDocumento estadoVigencia, DateOnly? fechaVencimiento, DateOnly hoy) =>
        // Los umbrales no afectan a "Vencido"; 0/0 basta y evita leer ParametrosSistema.
        CalculadoraEstadoDocumento.Calcular(estadoVigencia, fechaVencimiento, hoy, 0, 0) != EstadoDocumento.Vencido;

    /// <summary>Los candidatos en orden de preferencia: el primero es el efectivo.</summary>
    public static IOrderedEnumerable<T> Ordenar<T>(
        IEnumerable<T> candidatos,
        Func<T, EstadoVigenciaDocumento> estadoVigencia,
        Func<T, DateOnly?> fechaVencimiento,
        Func<T, DateOnly> fechaEmision,
        Func<T, DateTime> creadoEnUtc,
        Func<T, Guid> id,
        DateOnly hoy,
        Func<T, bool>? esNominativo = null) =>
        candidatos
            .OrderByDescending(d => ValidoHoy(estadoVigencia(d), fechaVencimiento(d), hoy))
            .ThenByDescending(d => esNominativo?.Invoke(d) ?? false)
            .ThenByDescending(fechaEmision)
            .ThenByDescending(d => estadoVigencia(d) != EstadoVigenciaDocumento.SinConfirmar)
            .ThenByDescending(creadoEnUtc)
            .ThenBy(id);

    /// <summary>Un documento efectivo por clave (p. ej. por Tipo): el primero de cada grupo.</summary>
    public static Dictionary<TClave, T> UnoPorClave<T, TClave>(
        IEnumerable<T> candidatos,
        Func<T, TClave> clave,
        Func<T, EstadoVigenciaDocumento> estadoVigencia,
        Func<T, DateOnly?> fechaVencimiento,
        Func<T, DateOnly> fechaEmision,
        Func<T, DateTime> creadoEnUtc,
        Func<T, Guid> id,
        DateOnly hoy,
        Func<T, bool>? esNominativo = null) where TClave : notnull =>
        candidatos
            .GroupBy(clave)
            .ToDictionary(g => g.Key, g => Ordenar(g, estadoVigencia, fechaVencimiento, fechaEmision, creadoEnUtc, id, hoy, esNominativo).First());
}
