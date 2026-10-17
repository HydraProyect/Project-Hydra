namespace CaeManager.Web.Components.DesignSystem;

public enum TamanoAvatarUsuario
{
    /// <summary>24 px: dentro de una fila de listado, junto al nombre.</summary>
    Pequeno,

    /// <summary>32 px: líneas de tiempo y cabeceras.</summary>
    Medio,

    /// <summary>56 px: donde el avatar es el protagonista (la pantalla en la que se elige).</summary>
    Grande
}

/// <summary>
/// Quién es el autor que pinta el avatar. Una persona con cuenta es el caso normal; las otras dos
/// son marcas de «autor» que no son una persona viva (revisión de Design, 2026-10-17, §A).
/// </summary>
public enum VarianteAvatarUsuario
{
    /// <summary>Una persona con cuenta: su avatar del catálogo o, si no eligió, sus iniciales.</summary>
    Persona,

    /// <summary>Acción automática de TALVEG (vencimientos, relevos): un rombo en el cian de sistema.</summary>
    Sistema,

    /// <summary>Una cuenta que ya no existe: borde discontinuo y «?» en vez de las iniciales de un nombre borrado.</summary>
    Eliminado
}
