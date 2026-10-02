namespace CaeManager.Domain.Cumplimiento;

/// <summary>
/// Versión vigente de los Términos y Condiciones + Política de Privacidad
/// (`Project-Hydra-Negocio/legal/TERMINOS_Y_CONDICIONES.md` / `Project-Hydra-Negocio/legal/POLITICA_PRIVACIDAD.md`,
/// mostrados en <c>/legal/terminos</c> y <c>/legal/privacidad</c>). Subir
/// este valor obliga a todo usuario a volver a aceptar: es la única palanca
/// que decide "hace falta re-aceptación". Se sube en el mismo cambio que
/// edite el contenido legal mostrado en esas páginas, nunca antes ni después.
/// Vale también para una corrección de texto que parezca prometer menos: una
/// redacción nueva sobre transferencias fuera del EEE se trata como cambio
/// material (decisión del propietario, 2026-09-20).
///
/// <para>
/// Historial: <c>2026-09-20</c> (versión con los textos de transferencias fuera del EEE);
/// <c>2026-10-02</c> (cambio de marca: el nombre del producto y de la entidad que presta el servicio
/// pasa a ser TALVEG en los Términos y en la Política de Privacidad, y el texto de aceptación del
/// gate interpola <c>Marca.Nombre</c>, que ya vale TALVEG; quien aceptó la versión anterior aceptó
/// otro texto y vuelve a aceptar).
/// </para>
/// </summary>
public static class VersionTerminos
{
    public const string Actual = "2026-10-02";
}
