namespace CaeManager.Application.EstadoMaterializado.Frescura;

/// <summary>
/// Aviso de que el estado materializado de un Tenant propietario cambió (diseño § 5.3): el Tenant, la versión de
/// <c>VersionesEstadoTenant</c> que dejó la transacción ya confirmada y las entidades (Centros, Trabajadores, Empresas o
/// Subcontratas) cuyas filas de estado escribió.
///
/// <para>
/// <b>Nunca lleva datos de estado.</b> Quien lo recibe no pinta nada con él: relee esas filas con su propio contexto RLS y
/// su alcance, así que un aviso que llegara a un circuito equivocado solo provoca una lectura que no devuelve nada.
/// </para>
/// </summary>
/// <param name="TenantId">Tenant propietario de las filas escritas.</param>
/// <param name="Version">La versión del Tenant tras esa transacción (la que devolvió su <c>UPDATE … RETURNING</c>).</param>
/// <param name="Entidades">Identificadores de las entidades cuyas filas de estado escribió la transacción.</param>
public sealed record AvisoCambioEstado(Guid TenantId, long Version, IReadOnlyCollection<Guid> Entidades);
