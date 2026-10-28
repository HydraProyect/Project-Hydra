namespace CaeManager.Domain.Tenants;

/// <summary>
/// Contactos de seguridad del Tenant actual. El filtro de Tenant lo pone el contexto (y la RLS): nada de
/// aquí lee ni escribe filas de otro Tenant. Lo que hace el Actor de Plataforma TALVEG sobre todos los
/// Tenants va por <c>IContactosSeguridadPlataforma</c> (Application).
/// </summary>
public interface IContactoSeguridadTenantRepository
{
    /// <summary>Las filas Propuesta y Validada del Tenant actual (como mucho una de cada).</summary>
    Task<IReadOnlyList<ContactoSeguridadTenant>> ObtenerPendienteYVigenteAsync(CancellationToken cancellationToken);

    /// <summary>La última fila Rechazada del Tenant actual, o null.</summary>
    Task<ContactoSeguridadTenant?> ObtenerUltimoRechazadoAsync(CancellationToken cancellationToken);

    void Agregar(ContactoSeguridadTenant contacto);

    /// <summary>
    /// Guarda la propuesta y la sustitución de la pendiente. Devuelve <c>false</c>, sin guardar nada, si otra
    /// propuesta del mismo Tenant ganó la carrera (violación del índice único de una Propuesta por Tenant);
    /// cualquier otro error se propaga. Una carrera que pase a Sustituida la misma fila pendiente acaba en
    /// <c>DbUpdateConcurrencyException</c>, que traduce <c>ConcurrenciaBehavior</c>.
    /// </summary>
    Task<bool> GuardarPropuestaAsync(CancellationToken cancellationToken);
}
