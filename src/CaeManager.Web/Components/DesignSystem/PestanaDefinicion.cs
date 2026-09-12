namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Una pestaña de <see cref="Pestanas"/> — Id estable (usado en la URL/estado),
/// Etiqueta visible y, opcionalmente, el nombre de un icono del catálogo.
///
/// <para>
/// El icono es opcional y se decide <b>por tira completa</b>, no por pestaña:
/// una sola pestaña con icono en una fila que no los lleva se lee como un
/// defecto, no como énfasis. O las lleva todas o ninguna.
/// </para>
/// </summary>
public record PestanaDefinicion(string Id, string Etiqueta, string? Icono = null)
{
    /// <summary>
    /// Píldora con un recuento a la derecha de la etiqueta (mockup Gen 2 de
    /// Trabajador 360: «Operación 3»). Opcional: sin valor la pestaña se
    /// pinta exactamente como antes y ningún consumidor existente cambia.
    /// </summary>
    public ContadorPestana? Contador { get; init; }
}

/// <summary>
/// El recuento de una pestaña. <paramref name="Glosa"/> es obligatoria y no
/// decorativa: es lo único que convierte «3» en «3 documentos con incidencia»
/// para quien no ve el color de la píldora (02 § 8 — el color nunca es el
/// único portador de significado). Se escribe en singular o plural ya
/// resuelto por el llamador, que es quien conoce la unidad.
/// </summary>
/// <param name="Valor">El número que se pinta.</param>
/// <param name="Glosa">Qué cuenta ese número, para el nombre accesible.</param>
/// <param name="EnAlerta">El recuento es de algo que va mal — la píldora pasa a tono de peligro.</param>
public record ContadorPestana(int Valor, string Glosa, bool EnAlerta = false);
