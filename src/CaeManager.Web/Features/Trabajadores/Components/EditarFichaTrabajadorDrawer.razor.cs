namespace CaeManager.Web.Features.Trabajadores.Components;

public partial class EditarFichaTrabajadorDrawer
{
    /// <summary>
    /// El paso donde está lo que rechazó el servidor (documento, marca de extranjero cobrable, Delegación y Oficio); nulo si no
    /// es de un campo. Los códigos son los literales de error que devuelven el handler de la edición y la resolución de la
    /// asignación de Delegación y Oficio: aquí no se renombran.
    /// </summary>
    private static string? PasoDelCodigoDeError(string codigo) => codigo switch
    {
        "Trabajador.DocumentoNoValido" or "Trabajador.DocumentoSinGestion" or "Trabajador.DniDuplicado" => PasoBasico,
        "Trabajador.SinEmpresa" or "Delegacion.NoEncontrada" or "Oficio.NoEncontrado" => PasoTrabajo,
        "Trabajador.ExtranjeroCobrableSinGestion" => PasoSituacion,
        _ => null,
    };
}
