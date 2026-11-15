using CaeManager.Application.Common.Archivos;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Architecture.Tests.Soporte;

/// <summary>
/// Controles sintéticos de <see cref="ReflexionArquitecturaHelper.TiposQueNombranTipoEnIl"/>: piden un puerto que cruza
/// Tenants solo dentro del cuerpo de un método, sin constructor, campo, propiedad ni firma que lo nombre. Es la vía que la
/// reflexión de firmas no ve; si el instrumento de IL dejara de verla, sus controles caerían. No los usa nada fuera de los
/// tests de arquitectura.
/// </summary>
internal static class ConsumidorSinteticoDeOriginalesInbound
{
    public static object Pedir(IServiceProvider servicios) => servicios.GetRequiredService<IAlmacenOriginalesInbound>();
}

/// <inheritdoc cref="ConsumidorSinteticoDeOriginalesInbound"/>
internal static class ConsumidorSinteticoDeLaLecturaEntreTenants
{
    public static object Pedir(IServiceProvider servicios) => servicios.GetService(typeof(IAlmacenArchivosDelTenantPropietario))!;
}
