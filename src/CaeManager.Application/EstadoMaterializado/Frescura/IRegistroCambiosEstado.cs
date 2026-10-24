namespace CaeManager.Application.EstadoMaterializado.Frescura;

/// <summary>
/// Punto de enganche del escritor del estado materializado con el aviso a los circuitos (diseño § 5.3). Scoped: el del
/// mismo ámbito que el <c>DbContext</c> con el que se escribe.
///
/// <para>
/// <b>Contrato para quien escribe estado.</b> Una llamada a <see cref="Anotar"/> por cada subida de
/// <c>VersionesEstadoTenant.Version</c>, dentro de la misma transacción y después de la subida, con la versión que
/// devolvió esa sentencia (<c>UPDATE … RETURNING "Version"</c>, nunca una lectura aparte, que podría ver la de otra
/// transacción) y las entidades cuyas filas escribió. Si el escritor no sube la versión (no cambió ninguna fila), no anota.
/// La caché da por cubierta una versión solo si recibió su aviso; una versión sin aviso (otro contenedor, aviso perdido)
/// obliga a releer entero, que es lo seguro, así que un aviso de menos nunca deja datos viejos y uno con entidades de menos
/// sí: por eso las entidades tienen que ser todas las escritas.
/// </para>
///
/// <para>
/// El aviso sale después del <c>COMMIT</c>; si la transacción se deshace, o se vuelve a un punto de guardado anterior a la
/// anotación, se descarta. Fuera de transacción (sentencia ya confirmada) sale en el acto.
/// </para>
/// </summary>
public interface IRegistroCambiosEstado
{
    /// <summary>Anota el aviso de la subida de versión que la transacción en curso acaba de hacer.</summary>
    void Anotar(AvisoCambioEstado aviso);
}
