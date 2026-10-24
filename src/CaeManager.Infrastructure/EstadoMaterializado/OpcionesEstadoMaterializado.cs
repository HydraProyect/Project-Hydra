namespace CaeManager.Infrastructure.EstadoMaterializado;

/// <summary>
/// Configuración del estado materializado (sección <see cref="Seccion"/>).
/// </summary>
public sealed class OpcionesEstadoMaterializado
{
    public const string Seccion = "EstadoMaterializado";

    /// <summary>
    /// Si la cola rellena los Tenants que aún no tienen fila en <c>VersionesEstadoTenant</c>. Apagado por defecto: en cuanto un
    /// Tenant tiene versión, los lectores de Centro leen lo guardado, y los huecos conocidos del detector (cambios de otro
    /// Tenant, contextos creados a mano, fallos que no se pueden encolar) no se reponen hasta el cotejo de la PR 5 del plan.
    /// Se enciende cuando el cotejo esté en main. La cola de los Tenants que ya tienen versión se procesa siempre.
    /// </summary>
    public bool RellenoAutomatico { get; set; }
}
