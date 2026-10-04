namespace CaeManager.Domain.Auditoria;

/// <summary>
/// Una entidad que, en el guardado que la modifica, pide que su fila de auditoría
/// lleve una acción propia (p. ej. <see cref="RegistroAuditoria.AccionRestaurado"/>)
/// en lugar del genérico «Modificado». La fila sigue escribiéndola el
/// interceptor, en la misma transacción que el cambio y con el mismo Actor real,
/// Usuario simulado y vía de acceso: lo único que cambia es el nombre de la acción.
///
/// <para>
/// La marca es transitoria (no se persiste) y de un solo uso:
/// el interceptor la consume al auditar, de modo que una modificación posterior de
/// la misma instancia vuelve a ser «Modificado».
/// </para>
/// </summary>
public interface IAccionAuditoriaPropia
{
    /// <summary>Devuelve la acción pendiente y la borra; <c>null</c> si no hay ninguna.</summary>
    string? ConsumirAccionAuditoria();
}
