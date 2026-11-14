namespace CaeManager.Application.Common;

/// <summary>
/// Puerto del Núcleo por el que el módulo Facturación (D-M1 y D-F2, 2026-11-14) lee lo que necesita de la liquidación
/// mensual del Operador CAE externo sin depender de Outbound: quién emite, qué meses están terminados y a qué Tenants
/// beneficiarios gestionó el emisor en un mes. Lo implementa hoy Outbound
/// (<c>Application/Dashboard/TarifasOperadorCae/FuenteLiquidacionOperadorCae</c>), donde viven las tarifas, las
/// Asignaciones de Operación y el Cuadro de dirección.
///
/// <para>
/// El emisor es siempre el Tenant de origen de quien pide, con el rol efectivo en ese Tenant: ningún método acepta un
/// TenantId del llamante como autoridad, y lo que devuelve ya está acotado a ese emisor (y a la RLS de su Tenant).
/// </para>
/// </summary>
public interface IFuenteLiquidacionOperadorCae
{
    /// <summary>
    /// El Tenant de origen del Operador CAE si quien pide puede leer sus liquidaciones (Administrador, Dirección CAE o
    /// Coordinador CAE en ese Tenant, D-F9); null si no, o si entra por Sesión Privilegiada.
    /// </summary>
    Task<Guid?> EmisorQueLeeAsync();

    /// <summary>
    /// El Tenant de origen del Operador CAE si quien pide puede emitir, exportar y marcar sus liquidaciones (Administrador o
    /// Dirección CAE en ese Tenant, D-F9); null si no.
    /// </summary>
    Task<Guid?> EmisorQueEmiteAsync();

    /// <summary>El instante en que termina el mes (hora de negocio, en UTC); null si el año y el mes no forman un mes válido.</summary>
    DateTime? FinDelMesUtc(int anyo, int mes);

    /// <summary>
    /// Los Tenants beneficiarios que tienen liquidación del emisor ese mes: exactamente las Empresas que la pantalla
    /// «Facturación» lista y el CSV exporta (gestionadas en algún tramo del mes por una Asignación de Operación externa y
    /// presentes en la serie del Cuadro de dirección, es decir, autorizadas por la vía Outbound y alcanzadas enteras por
    /// quien pide). Vacío si quien pide no lee las liquidaciones o el mes no es válido. Nunca incluye al propio emisor.
    /// </summary>
    Task<IReadOnlySet<Guid>> TenantsBeneficiariosDelMesAsync(int anyo, int mes, CancellationToken cancellationToken);
}
