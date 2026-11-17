using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.AutorizacionesLectura;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.AutorizacionesLectura.Queries.TieneAutorizacionLecturaConcedida;

/// <summary>
/// ¿Ha concedido el Tenant activo, como Tenant propietario que concede la autorización, alguna autorización de lectura documental que siga concedida
/// (<see cref="AutorizacionLecturaDocumental.EstaVigenteEn"/>: sin revocar y dentro de su vigencia hoy)? Es la señal con la que la
/// interfaz decide si el menú lateral enseña el grupo «Mis Clientes» (decisión D6 de la demo de Inbound,
/// 2026-11-17: un Tenant ve el menú del Tenant TEC si tiene al menos una autorización concedida como Tenant propietario). Una
/// autorización revocada, caducada o que todavía no ha empezado no cuenta.
///
/// <para>
/// Presentación y nada más: no concede ni quita ninguna lectura. Las páginas del grupo se autorizan por su cuenta (hoy con
/// <c>[Authorize(Roles = Administrador)]</c>; la capacidad <see cref="CapacidadInbound.ParticipanteTec"/> la piden las peticiones
/// del grupo y las páginas la aplicarán al cablear el backend) y el gate de la lectura entre Tenants lo impone la base (<c>app_inbound_*</c>), que exige además el contrato
/// de acceso vigente y vinculado; por eso el criterio no es el de «ese Cliente empresarial puede leer hoy», sino el más amplio
/// de «este Tenant ha autorizado y no ha retirado esa autorización».
/// </para>
///
/// <para>
/// Se lee en local, con el filtro de Tenant y la RLS normales: solo las filas del propio Tenant. El filtro por el Tenant activo
/// se repite aquí para que la respuesta no dependa de que el contexto de lectura lo aplique. Falla cerrado: sin Tenant activo
/// responde <c>false</c>. Pide la misma capacidad que las peticiones del grupo, como <c>ObtenerAutorizacionesLecturaQuery</c>
/// (<see cref="CapacidadInbound.ParticipanteTec"/>: el Administrador del Tenant TEC, en su propio Tenant o con papel Outbound;
/// nunca con papel Inbound ni sin papel), para que el menú no enseñe un grupo cuyos datos el servidor va a devolver vacíos;
/// sin ella responde <c>false</c>. El rol Administrador lo sigue
/// exigiendo además el menú. Con un Encargo de administración no pleno el área entera queda fuera (como
/// el resto de las peticiones de <c>AutorizacionesLectura</c>) y la interfaz lo lee como «no».
/// </para>
/// </summary>
[AreaDelEncargo("AutorizacionesLectura")]
public record TieneAutorizacionLecturaConcedidaQuery : IRequest<bool>;

public class TieneAutorizacionLecturaConcedidaQueryHandler(
    IAutorizacionesLecturaQueryContext contexto, ITenantActual tenantActual, TimeProvider reloj,
    IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<TieneAutorizacionLecturaConcedidaQuery, bool>
{
    public async Task<bool> Handle(TieneAutorizacionLecturaConcedidaQuery request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.ParticipanteTec, cancellationToken))
            return false;

        if (tenantActual.TenantId is not { } tenantId)
            return false;

        var hoy = DiaDeNegocio.Hoy(reloj);
        return await contexto.AutorizacionesLecturaDocumental
            .Where(a => a.TenantId == tenantId)
            .Where(AutorizacionLecturaDocumental.EstaVigenteEn(hoy))
            .AnyAsync(cancellationToken);
    }
}
