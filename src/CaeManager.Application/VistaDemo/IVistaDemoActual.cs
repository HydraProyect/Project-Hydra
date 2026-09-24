namespace CaeManager.Application.VistaDemo;

/// <summary>La vista que de verdad se aplica a esta petición — ya validada, nunca el valor crudo de la cookie.</summary>
public sealed record VistaDemoEfectiva(VistaDemo Vista, Guid? GestorUsuarioId);

/// <summary>Un Gestor CAE del Operador CAE de la cuenta de demo cuya cartera puede mostrarse en la lente.</summary>
public sealed record GestorDeVistaDemo(Guid UsuarioId, string Nombre);

/// <summary>
/// Lo que la sesión pide: la vista de la cookie. El rol que decide la disponibilidad NO viaja aquí:
/// lo lee <c>VistaDemoActual</c> con <c>ICurrentUserService.ObtenerRolOrigenAsync</c> (decisión P7,
/// 2026-09-23) — el claim de sesión ya está sustituido por el rol de la cartera dentro de un
/// Workspace operativo derivado.
/// </summary>
/// <param name="Vista">Vista pedida, o null si no hay cookie válida (ausente, manipulada, de otra cuenta, caducada).</param>
/// <param name="GestorUsuarioId">Gestor CAE pedido para la vista Gestor; solo tiene sentido con <see cref="VistaDemo.GestorCae"/>.</param>
public sealed record PeticionVistaDemo(VistaDemo? Vista, Guid? GestorUsuarioId);

/// <summary>
/// Lo que la sesión HTTP pide. Lo implementa Web (cookie firmada con Data Protection, ligada al
/// usuario); Application/Infrastructure NO se fían de ello: <see cref="IVistaDemoActual"/> lo valida
/// entero antes de aplicar nada.
/// </summary>
public interface ISolicitudVistaDemo
{
    Task<PeticionVistaDemo> ObtenerAsync();
}

/// <summary>
/// Resuelve la lente de demo que se aplica a la petición actual. Todo devuelve
/// "sin lente" (<c>null</c>/<c>false</c>) salvo que se cumplan a la vez: la
/// función está activada por configuración (apagada por defecto), la sesión no
/// es privilegiada de plataforma, la cuenta tiene el rol de sesión
/// Administrador o DireccionCae, y su Tenant de origen es un Tenant de demo
/// (<c>RetiradaTenantDemoService.NombresTenantsDeDemo</c> — el único marcador de
/// demo del sistema, deliberadamente una allowlist exacta). Para la vista
/// Gestor, además, el Tenant activo debe ser de demo y el Gestor pedido debe
/// ser uno de los elegibles del Operador CAE de la cuenta.
///
/// "Sin lente" significa la autorización real tal cual: fallar aquí nunca
/// amplía nada, y nada aquí puede sustituir a la autorización real.
/// </summary>
public interface IVistaDemoActual
{
    /// <summary>
    /// Si esta cuenta puede ver el selector. Depende SOLO de la identidad real y
    /// del Tenant de origen — nunca de la vista aplicada ni del Tenant activo —,
    /// para que ninguna vista pueda esconder el selector y dejarlo enganchado.
    /// </summary>
    Task<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default);

    /// <summary>La lente que se aplica ahora, o null si no hay ninguna (autorización real sin acotar).</summary>
    Task<VistaDemoEfectiva?> ObtenerEfectivaAsync(CancellationToken cancellationToken = default);

    /// <summary>Los Gestores CAE que el selector puede ofrecer: los del Operador CAE de la cuenta con Asignación de Cartera vigente.</summary>
    Task<IReadOnlyList<GestorDeVistaDemo>> ObtenerGestoresElegiblesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tenants propietarios donde la lente Gestor tiene cartera vigente, para acotar la lista
    /// multi-Tenant del Gestor; null si la lente no acota Tenants. Solo puede quitar Tenants a
    /// los que la cuenta ya alcanza, nunca añadir uno.
    /// </summary>
    Task<IReadOnlyList<Guid>?> ObtenerTenantIdsAcotadosAsync(CancellationToken cancellationToken = default);
}
