namespace CaeManager.Domain.Operaciones;

/// <summary>
/// Producto/módulo TALVEG al que se refiere una asignación de responsabilidad
/// operativa — ver ADR-011 § 2.6. Outbound e Inbound comparten núcleo de
/// dominio pero son productos independientes: una Empresa puede contratar uno,
/// otro o ambos, y tener operadores distintos en cada uno.
///
/// Hoy solo se emite <see cref="Outbound"/>: <see cref="Inbound"/> existe en el
/// enum para que el ámbito de toda asignación quede acotado a un servicio desde
/// el primer día (la semántica de un ámbito es siempre relativa a la terna
/// propietario + servicio + asignación), no porque el producto exista. F1 no
/// implementa Inbound.
/// </summary>
public enum ServicioCae
{
    /// <summary>
    /// El flujo del contratista que acredita a su gente ante los centros y
    /// portales de sus clientes titulares. Es todo lo construido hoy.
    /// </summary>
    Outbound = 0,

    /// <summary>
    /// El flujo del titular que valida la documentación de quienes acceden a
    /// sus centros. Producto futuro (ADR-011 § 6).
    /// </summary>
    Inbound = 1
}
