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
    /// Tenant, contextos creados a mano, fallos que no se pueden encolar) solo los repone el cotejo nocturno
    /// (<see cref="CambioDeDiaEstadoMaterializado"/>), una vez al día. Con el cotejo en main ya se puede encender; encenderlo
    /// en producción lo decide Chris. La cola de los Tenants que ya tienen versión se procesa siempre.
    /// </summary>
    public bool RellenoAutomatico { get; set; }
}
