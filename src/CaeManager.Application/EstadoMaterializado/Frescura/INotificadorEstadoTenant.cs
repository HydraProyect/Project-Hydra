namespace CaeManager.Application.EstadoMaterializado.Frescura;

/// <summary>
/// Aviso en proceso a los circuitos abiertos cuando sube la versión del estado materializado de un Tenant (diseño § 5.3).
/// Singleton. Es una mejora, no la garantía: la garantía es que <see cref="CacheEstadoCircuito"/> lee la versión en cada
/// lectura, y eso no depende de en qué contenedor esté el circuito (en un relevo azul/verde conviven dos).
/// </summary>
public interface INotificadorEstadoTenant
{
    /// <summary>
    /// Suscribe <paramref name="alRecibir"/> a los avisos de <paramref name="tenantId"/>; la suscripción acaba al liberar
    /// lo devuelto. El callback corre en el hilo de quien publica (otro circuito u otro trabajo): debe ser breve, no lanzar
    /// y no tocar el contexto de su propio circuito sin pasar por su despachador.
    /// </summary>
    IDisposable Suscribir(Guid tenantId, Action<AvisoCambioEstado> alRecibir);

    /// <summary>Entrega <paramref name="aviso"/> a las suscripciones de su Tenant y solo a ellas. Nunca lanza.</summary>
    void Publicar(AvisoCambioEstado aviso);

    /// <summary>Cuántas suscripciones vivas tiene <paramref name="tenantId"/> (diagnóstico y tests de aislamiento).</summary>
    int SuscripcionesDe(Guid tenantId);
}
