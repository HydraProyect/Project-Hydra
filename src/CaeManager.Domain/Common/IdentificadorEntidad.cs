namespace CaeManager.Domain.Common;

/// <summary>
/// Única fuente de identificadores de entidad (P1-M3). Genera UUID v7
/// (RFC 9562): los 48 bits altos son la marca de tiempo en milisegundos, así
/// que las filas nuevas se insertan al final del índice de la clave primaria
/// en vez de en un punto aleatorio — menos páginas tocadas y menos
/// fragmentación, sobre todo en las tablas grandes y particionadas por fecha
/// (<c>RegistrosAuditoria</c>, <c>RegistrosAccesoDocumentoSensible</c>, con
/// clave <c>(Id, FechaUtc)</c>).
///
/// Un Id v7 NO es un secreto: deja ver cuándo se creó la fila y sus bits
/// aleatorios (74) no deben tratarse como capacidad. Nada que actúe como
/// token, sello, nonce o enlace no adivinable sale de aquí: esos siguen con
/// <see cref="Guid.NewGuid"/> o con <c>RandomNumberGenerator</c>. Lo vigila
/// <c>IdentificadoresDeEntidadUuidV7Tests</c> (Architecture.Tests).
///
/// Los Id anteriores (v4) se quedan como están: ninguna clave se reescribe, y
/// v4 y v7 conviven en la misma columna <c>uuid</c>. Nada puede, por tanto,
/// deducir la antigüedad de una fila a partir de su Id.
/// </summary>
public static class IdentificadorEntidad
{
    public static Guid Nuevo() => Guid.CreateVersion7();
}
