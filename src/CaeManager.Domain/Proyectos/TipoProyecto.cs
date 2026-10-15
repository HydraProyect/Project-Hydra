namespace CaeManager.Domain.Proyectos;

/// <summary>
/// Clase de Proyecto. Valores explícitos y congelados: se guardan como entero en
/// <c>Proyectos.Tipo</c>, y las filas anteriores a la columna quedan como <see cref="Obra"/>.
/// </summary>
public enum TipoProyecto
{
    /// <summary>Obra, instalación o mantenimiento en un Centro: el Proyecto de siempre.</summary>
    Obra = 0,

    /// <summary>
    /// Un evento con fecha de inicio y de fin (un partido, una feria) en un Centro, con su propio listado de
    /// quién entra (Trabajadores y, a través de ellos, sus Empresas). Sus requisitos documentales viven en el
    /// propio evento (<see cref="ProyectoRequisito"/>) y se copian de otro evento o de una
    /// <see cref="PlantillaRequisitosEvento"/>. La participación en un evento no crea una Asignación al
    /// Centro: quien solo entró por eventos ya terminados no figura entre los Trabajadores del Centro.
    /// </summary>
    Evento = 1,
}
