namespace CaeManager.Application.Common;

/// <summary>
/// Marcador: este <see cref="ICommand"/> es el acto que una Sesión Privilegiada con
/// capacidad <c>RevocacionOperadorCaeExterno</c> puede ejecutar sobre su Tenant objetivo
/// (ADR-011 § 8.7, punto 3; enmienda de ADR-004 § 12.2 del 2026-10-26). Hoy, y por diseño,
/// uno solo: <c>RevocarOperadorCaeExternoPorSoporteCommand</c>.
///
/// <para>
/// Separado de <see cref="IComandoDeRestablecimientoSegundoFactor"/> y de
/// <see cref="IComandoDeAprovisionamiento"/> a propósito: cada capacidad acotada abre su
/// propio acto y ninguno de los demás.
/// </para>
///
/// <para>
/// Sin miembros. <c>MarcadorDeRevocacionOperadorCaeExternoTests</c> congela el inventario:
/// un segundo comando que lo implemente tiene que verse en la revisión de ese test.
/// </para>
/// </summary>
public interface IComandoDeRevocacionOperadorCaeExterno;
