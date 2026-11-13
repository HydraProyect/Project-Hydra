namespace CaeManager.Application.Common;

/// <summary>
/// Marcador: este <see cref="ICommand"/> es el acto que una Sesión Privilegiada con capacidad
/// <c>AjusteSegundoFactorTenant</c> puede ejecutar sobre su Tenant objetivo (ADR-011 § 8.7, punto 3;
/// decisión del 2026-11-08). Hoy, y por diseño, uno solo:
/// <c>CambiarSegundoFactorDelTenantPorPlataformaCommand</c>.
///
/// <para>
/// Separado de los marcadores de las otras capacidades a propósito: cada capacidad acotada abre
/// su propio acto y ninguno de los demás.
/// </para>
///
/// <para>
/// Sin miembros. <c>MarcadorDeAjusteSegundoFactorTenantTests</c> congela el inventario: un segundo
/// comando que lo implemente tiene que verse en la revisión de ese test.
/// </para>
/// </summary>
public interface IComandoDeAjusteSegundoFactorTenant;
