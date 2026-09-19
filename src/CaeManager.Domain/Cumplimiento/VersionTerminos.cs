namespace CaeManager.Domain.Cumplimiento;

/// <summary>
/// Versión vigente de los Términos y Condiciones + Política de Privacidad
/// (`docs/business/legal/TERMINOS_Y_CONDICIONES.md` / `POLITICA_PRIVACIDAD.md`,
/// mostrados en <c>/legal/terminos</c> y <c>/legal/privacidad</c>). Subir
/// este valor obliga a todo usuario a volver a aceptar: es la única palanca
/// que decide "hace falta re-aceptación". Se sube en el mismo cambio que
/// edite el contenido legal mostrado en esas páginas, nunca antes ni después.
/// Vale también para una corrección de texto que parezca prometer menos: una
/// redacción nueva sobre transferencias fuera del EEE se trata como cambio
/// material (decisión del propietario, 2026-09-20).
/// </summary>
public static class VersionTerminos
{
    public const string Actual = "2026-09-20";
}
