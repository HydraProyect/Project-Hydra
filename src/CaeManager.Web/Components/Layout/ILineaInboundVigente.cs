namespace CaeManager.Web.Components.Layout;

/// <summary>
/// ¿Tiene el Tenant beneficiario activa la línea Inbound en su Producto Contratado? Es el gate de la sección «Inbound» del
/// menú lateral y de los destinos de Inbound fuera de él (<see cref="AdmisionDeDestinos"/>); las páginas de Inbound no lo
/// comprueban y se apoyan en la autorización de sus Commands y Queries.
///
/// <para>
/// Puerto del Núcleo: el menú lateral no conoce a Inbound (ADR-013 § 4.1, regla 3), así que la interfaz vive aquí y Inbound
/// registra su implementación (<c>Features.Inbound.LineaInboundVigenteDelProductoContratado</c>, que despacha
/// <c>LineaInboundVigenteQuery</c>).
/// </para>
///
/// <para>
/// Es el plano comercial (Producto Contratado), no la propiedad de los datos ni la operación: no sustituye a la autoridad de
/// cada Query y Command, que sigue siendo la suya (Cliente empresarial visible), ni al gate de la lectura entre Tenants,
/// que comprueba la base de datos (DN-7: <c>app_inbound_*</c>) con el mismo criterio. Falla cerrado: ante un error, no.
/// </para>
/// </summary>
public interface ILineaInboundVigente
{
    Task<bool> EstaVigenteAsync(CancellationToken cancellationToken = default);
}
