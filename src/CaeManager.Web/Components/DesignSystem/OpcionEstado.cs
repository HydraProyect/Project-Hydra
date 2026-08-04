namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Una opción de <see cref="FiltroEstado"/> — <c>Valor</c> es lo que viaja a
/// la URL y a la Query (el nombre del miembro del enum), <c>Texto</c> lo que
/// lee el gestor. Las listas concretas viven en el <c>Estado*Ui</c> de cada
/// entidad, que ya es el sitio único donde un estado se traduce a texto.
/// </summary>
public record OpcionEstado(string Valor, string Texto);
