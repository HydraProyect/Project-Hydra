namespace CaeManager.Domain.Cumplimiento;

/// <summary>
/// Versión vigente de los Términos y Condiciones + Política de Privacidad
/// (`docs/business/legal/TERMINOS_Y_CONDICIONES.md` / `POLITICA_PRIVACIDAD.md`,
/// mostrados en <c>/legal/terminos</c> y <c>/legal/privacidad</c>). Subir
/// este valor obliga a todo usuario a volver a aceptar — es la única palanca
/// que decide "hace falta re-aceptación": cámbiala en el mismo cambio que
/// edite el contenido legal mostrado en esas páginas, nunca antes ni después.
/// </summary>
public static class VersionTerminos
{
    public const string Actual = "2026-08-08";
}
