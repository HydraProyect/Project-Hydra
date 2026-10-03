using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Visitas.PaqueteDocumental;

/// <summary>
/// Qué copia viaja en el paquete de acreditación cuando un (titular, tipo) tiene varias
/// copias vigentes. Decisión del propietario (2026-10-01): entre las copias vigentes de un
/// tipo, la de <b>emisión más reciente</b> (<c>Documento.FechaEmision</c>), aunque otra venza
/// más tarde. Es una regla propia del paquete: no es la de
/// <see cref="Documentos.PreferenciaDocumentoPorTipo"/>, que decide qué copia representa al
/// tipo en el estado y el cumplimiento y antepone la mayor vigencia.
///
/// <para>
/// Solo ordena copias que ya son vigentes —quien llama descarta las vencidas, con la misma
/// definición de «vencido» que el resto del sistema—. «Sin confirmar» y «No caduca» son
/// vigentes y compiten por su emisión como cualquier otra.
/// </para>
///
/// <para>
/// Orden (la primera es la que viaja): 1) <c>FechaEmision</c> más reciente; si coinciden,
/// 2) vigencia confirmada antes que «Sin confirmar»; 3) la que vence más tarde («No caduca»
/// cuenta como vigencia máxima); 4) la dada de alta más tarde (<c>CreadoEnUtc</c>);
/// 5) el menor <c>Id</c>. Los pasos 4 y 5 solo existen para que la elección no dependa del
/// orden en que devuelva las filas la base: con el mismo conjunto de copias, siempre viaja
/// la misma.
/// </para>
/// </summary>
public static class PreferenciaCopiaDelPaquete
{
    public static IOrderedEnumerable<T> Ordenar<T>(
        IEnumerable<T> vigentes,
        Func<T, DateOnly> fechaEmision,
        Func<T, EstadoVigenciaDocumento> estadoVigencia,
        Func<T, DateOnly?> fechaVencimiento,
        Func<T, DateTime> creadoEnUtc,
        Func<T, Guid> id) =>
        vigentes
            .OrderByDescending(fechaEmision)
            .ThenByDescending(d => estadoVigencia(d) != EstadoVigenciaDocumento.SinConfirmar)
            .ThenByDescending(d => fechaVencimiento(d) ?? DateOnly.MaxValue)
            .ThenByDescending(creadoEnUtc)
            .ThenBy(id);
}
