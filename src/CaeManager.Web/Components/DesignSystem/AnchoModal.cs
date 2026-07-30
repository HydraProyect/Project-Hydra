namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Ancho del contenido de <see cref="Modal"/> — Mediano (520px) es el valor
/// histórico y sigue siendo el default, así que ningún consumidor existente
/// cambia de tamaño. Grande es para contenido de solo lectura más denso
/// (p. ej. un detalle con varias columnas) sin llegar al ancho de un Drawer.
/// </summary>
public enum AnchoModal
{
    Pequeno,
    Mediano,
    Grande
}
