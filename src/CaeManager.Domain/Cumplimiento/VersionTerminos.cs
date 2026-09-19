namespace CaeManager.Domain.Cumplimiento;

/// <summary>
/// Versión vigente de los Términos y Condiciones + Política de Privacidad
/// (`docs/business/legal/TERMINOS_Y_CONDICIONES.md` / `POLITICA_PRIVACIDAD.md`,
/// mostrados en <c>/legal/terminos</c> y <c>/legal/privacidad</c>). Subir
/// este valor obliga a todo usuario a volver a aceptar: es la única palanca
/// que decide "hace falta re-aceptación". Se sube en el mismo cambio que
/// edite cualquier contenido contractual de esas páginas, nunca antes ni
/// después. Una rectificación de una afirmación inexacta que promete menos
/// que el texto ya aceptado no es un cambio contractual y no la sube; esa
/// decisión se anota en el DECISION_LOG (2026-09-19, P24 y D7).
/// </summary>
public static class VersionTerminos
{
    public const string Actual = "2026-08-08";
}
