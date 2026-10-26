namespace CaeManager.Infrastructure.Importacion;

/// <summary>
/// Configuración del lector de la plantilla «CAE completa» (sección <see cref="Seccion"/>):
/// qué hojas del libro listan Trabajadores y a qué Empresa pertenecen los de cada una.
///
/// La razón social de la Empresa de cada hoja es un dato del archivo que se importa, no del
/// producto: antes estaba escrita en el código, atada al primer archivo que se importó. Ahora
/// la declara quien configura el despliegue, y el lector no asume ninguna. Sin ninguna hoja
/// configurada el análisis no inventa una Empresa: lo dice en Omitidos y no lee Trabajadores.
/// </summary>
public sealed class OpcionesPlantillaCaeCompleta
{
    public const string Seccion = "Importacion:PlantillaCaeCompleta";

    /// <summary>Una entrada por hoja de Trabajadores del libro, en el orden en que se leen.</summary>
    public List<HojaTrabajadoresCae> HojasTrabajadores { get; set; } = [];
}

/// <summary>Una hoja de Trabajadores del libro y la Empresa a la que pertenecen todos los de esa hoja.</summary>
public sealed class HojaTrabajadoresCae
{
    /// <summary>Nombre exacto de la hoja en el libro.</summary>
    public string Hoja { get; set; } = "";

    /// <summary>Razón social de la Empresa de los Trabajadores de esa hoja.</summary>
    public string RazonSocialEmpresa { get; set; } = "";
}
