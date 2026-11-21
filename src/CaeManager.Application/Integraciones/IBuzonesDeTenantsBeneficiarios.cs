using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Puerto de lectura del Núcleo (Integraciones) por el que la pantalla de Buzones consulta los buzones de cada Tenant
/// beneficiario que la vista unificada de Comunicaciones (D-6 b) deja leer, rotulado «Empresa gestionada» (lienzo
/// «Comunicaciones multi-buzón», tablero de Buzones, estado L-55). Solo lectura.
///
/// <para>
/// <b>Por qué un puerto.</b> Como <see cref="IActividadBuzones"/>: la página es del Núcleo y la lectura entre Tenants es de
/// Comunicaciones (Outbound). Lo implementa Application.Comunicaciones enviando por el mediador las peticiones de la vista
/// unificada, así que pasa por su entrada (<c>EntradaVistaUnificada.AbrirAsync</c>: exclusiones de plano, conjunto de la
/// vista y lente de demo) y por el <c>AmbitoTenantAutorizado</c> de cada Tenant, bajo la RLS de ese Tenant. No hay otro
/// camino de lectura entre Tenants.
/// </para>
///
/// <para>
/// <b>El Tenant es una coordenada.</b> El que llega a <see cref="ObtenerBuzonesAsync"/> solo estrecha el conjunto calculado
/// en el servidor: uno de fuera, uno con la Asignación de Operación caducada o uno que no existe devuelven <c>null</c>, sin
/// distinguirse entre sí.
/// </para>
/// </summary>
public interface IBuzonesDeTenantsBeneficiarios
{
    /// <summary>
    /// Los Tenants beneficiarios del selector, por nombre, con el mismo conjunto que el selector de la bandeja unificada.
    /// Lanza <see cref="UnauthorizedAccessException"/> en los planos donde la vista no existe (Sesión Privilegiada, papel
    /// Inbound, Usuario de Cliente, Soporte TALVEG).
    /// </summary>
    Task<IReadOnlyList<TenantBeneficiarioDeBuzonesDto>> ObtenerTenantsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Los buzones de un Tenant beneficiario, con los nombres de quien responde y su actividad; <c>null</c> si la vista no
    /// lee ese Tenant para quien pregunta.
    /// </summary>
    Task<BuzonesDeTenantBeneficiarioDto?> ObtenerBuzonesAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <param name="TenantNombre"><c>Tenants.Nombre</c>, rótulo «Empresa gestionada» (DN-8).</param>
public record TenantBeneficiarioDeBuzonesDto(Guid TenantId, string TenantNombre);

/// <param name="Conexiones">
/// Los buzones compartidos y el personal propio del Tenant (la regla de <c>ObtenerConexionesIntegracionQuery</c> con
/// <c>SoloPropiasYGenerales</c>): el buzón personal de otro Gestor CAE no sale, tampoco para el Administrador.
/// </param>
/// <param name="Nombres">Nombres de responsables y propietarios visibles desde ese Tenant.</param>
/// <param name="Actividad">«Último correo» y «Abiertas» de cada buzón, con la regla de visibilidad de la vista unificada.</param>
public record BuzonesDeTenantBeneficiarioDto(
    IReadOnlyList<ConexionIntegracionListaDto> Conexiones,
    IReadOnlyDictionary<Guid, string> Nombres,
    IReadOnlyDictionary<Guid, ActividadBuzonDto> Actividad);
