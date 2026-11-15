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

/// <summary>
/// Como <see cref="ConsumidorSinteticoDeOriginalesInbound"/>, pero dentro de una lambda sin capturas: el compilador la
/// lleva a una clase anidada (<c>&lt;&gt;c</c>) y el método exterior no nombra el puerto. Si el escaneo dejara de recorrer
/// los tipos anidados o de contarlos como su tipo exterior, el control caería.
/// </summary>
internal static class ConsumidorSinteticoDeOriginalesInboundEnLambda
{
    public static Func<IServiceProvider, object> Pedir() => servicios => servicios.GetRequiredService<IAlmacenOriginalesInbound>();
}

/// <summary>
/// Como <see cref="ConsumidorSinteticoDeOriginalesInbound"/>, pero después de un <c>await</c>: la petición vive en el
/// <c>MoveNext</c> de la máquina de estados anidada, no en el método exterior.
/// </summary>
internal static class ConsumidorSinteticoDeOriginalesInboundAsincrono
{
    public static async Task<object> PedirAsync(IServiceProvider servicios)
    {
        await Task.Yield();
        return servicios.GetRequiredService<IAlmacenOriginalesInbound>();
    }
}

/// <inheritdoc cref="ConsumidorSinteticoDeOriginalesInbound"/>
internal static class ConsumidorSinteticoDeLaLecturaEntreTenants
{
    public static object Pedir(IServiceProvider servicios) => servicios.GetService(typeof(IAlmacenArchivosDelTenantPropietario))!;
}
