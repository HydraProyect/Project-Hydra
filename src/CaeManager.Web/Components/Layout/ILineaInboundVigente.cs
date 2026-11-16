namespace CaeManager.Web.Components.Layout;

/// <summary>
/// ¿Tiene el Tenant beneficiario activa la línea Inbound en su Producto Contratado? Es el gate de la sección «Inbound» del
/// menú lateral; las páginas de Inbound no lo comprueban y se apoyan en la autorización de sus Commands y Queries.
///
/// <para>
/// Puerto del Núcleo: el menú lateral no conoce a Inbound (ADR-013 § 4.1, regla 3), así que la interfaz vive aquí y Inbound
/// registra su implementación (<c>Features.Inbound.LineaInboundNoDisponible</c> hoy).
/// </para>
///
/// <para>
/// Es el plano comercial (Producto Contratado), no la propiedad de los datos ni la operación: no sustituye a la autoridad de
/// cada Query y Command, que sigue siendo la suya (Cliente empresarial visible). Hoy el gate de la línea solo lo comprueba
/// la base de datos (DN-7: <c>app_inbound_*</c>) y Application no tiene una Query de lectura de la línea contratada, así
/// que la implementación registrada falla cerrado.
/// </para>
/// </summary>
public interface ILineaInboundVigente
{
    Task<bool> EstaVigenteAsync(CancellationToken cancellationToken = default);
}
