namespace CaeManager.Web.Services;

/// <summary>
/// Por qué el usuario ha dejado de operar el Workspace operativo derivado que
/// tenía seleccionado y ha vuelto a su organización principal.
/// </summary>
public enum MotivoFinDeAcceso
{
    /// <summary>La ventana de soporte (sesión privilegiada o delegación de Soporte) terminó o se cerró.</summary>
    VentanaDeSoporte,

    /// <summary>Otra autorización (delegación, operación, cartera) dejó de estar vigente.</summary>
    AccesoNoVigente,
}

/// <summary>
/// Puente entre quien retira la selección y quien lo cuenta en pantalla.
/// <see cref="RevalidacionClienteActivoMiddleware"/> deja el motivo en
/// <c>HttpContext.Items</c> durante la petición que la retira, y
/// <c>AvisoFinDeAccesoEstatico</c> lo pinta en esa misma respuesta. Solo vive
/// esa petición: no hay cookie ni estado que sobreviva, así que el aviso
/// aparece una vez y no se arrastra.
/// </summary>
public static class AvisoFinDeAcceso
{
    public const string ClaveItems = "talveg.aviso-fin-de-acceso";

    public static string Texto(MotivoFinDeAcceso motivo) => motivo switch
    {
        MotivoFinDeAcceso.VentanaDeSoporte =>
            "La ventana de soporte terminó. Has vuelto a tu organización principal.",
        _ =>
            "Tu acceso a la organización que tenías seleccionada ya no está vigente. Has vuelto a tu organización principal.",
    };
}
