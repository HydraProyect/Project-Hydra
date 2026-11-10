namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Una opción de <see cref="CampoSelectAvanzado"/>. <paramref name="Valor"/> es lo que se
/// devuelve y se envía; <paramref name="Texto"/> lo que se lee. <paramref name="Descripcion"/> es la
/// línea secundaria opcional (la busca también el filtro). <paramref name="Tono"/> pinta un punto
/// de color con los mismos tonos semánticos de <see cref="Badge"/>: no es decorativo, así que solo
/// se usa cuando el color significa algo (p. ej. semáforo de vigencia). <paramref name="Grupo"/> es el
/// rótulo del grupo al que pertenece (lo que sería un <c>&lt;optgroup&gt;</c>): las opciones de un mismo
/// grupo van seguidas en <c>Opciones</c> y un grupo que el filtro deja vacío no se pinta. <paramref name="Alias"/>
/// son otros nombres por los que el filtro también la encuentra, sin enseñarlos.
/// </summary>
public sealed record OpcionSelect(
    string Valor,
    string Texto,
    string? Descripcion = null,
    TonoBadge? Tono = null,
    bool Deshabilitada = false,
    string? Grupo = null,
    IReadOnlyList<string>? Alias = null);
