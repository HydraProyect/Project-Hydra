namespace CaeManager.Web.Components.Account;

/// <summary>
/// Categoría de log compartida por los eventos de autenticación (login,
/// verificación de 2FA, activación de 2FA, cierre de sesión) — P1-13 de
/// docs/business/MATURITY_REVIEW.md. El login vive fuera del pipeline de
/// MediatR (es <c>SignInManager</c> directo desde componentes Razor y
/// endpoints minimal API, antes de que exista ningún request que pase por
/// <c>LoggingBehavior</c>), así que se audita con el mismo Serilog+Seq de
/// P1-10 pero con una categoría propia en vez de la de
/// <c>LoggingBehavior.CategoriaLog</c>.
/// </summary>
public static class AuditoriaAutenticacion
{
    public const string CategoriaLog = "CaeManager.Web.Autenticacion";
}
