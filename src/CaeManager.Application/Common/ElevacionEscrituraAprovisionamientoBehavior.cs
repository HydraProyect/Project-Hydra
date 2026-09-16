using CaeManager.Application.Plataforma;
using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Pipeline behavior de MediatR: el ÚNICO que abre
/// <see cref="AmbitoEscrituraPrivilegiada"/> — y por tanto el único que puede
/// hacer que una conexión adopte <c>cae_app_aprovisionamiento</c> en vez de
/// <c>cae_app_soporte</c> (ver <c>TenantRlsConnectionInterceptor</c>).
///
/// <b>Registrado EL ÚLTIMO del pipeline</b> (después de <c>ValidationBehavior</c>),
/// a propósito: reduce la ventana de rol elevado al handler solo. Otros
/// behaviors más externos —<c>ValidationBehavior</c> incluido— también pueden
/// tocar datos (p. ej. una regla de validación que consulta la base) y no hay
/// motivo para que corran con el rol de escritura acotada puesto.
///
/// <b>No decide nada</b>: repite las mismas cuatro condiciones que
/// <see cref="AutorizacionEscrituraBehavior"/> ya evaluó más afuera en el
/// mismo pipeline —hay sesión, tiene camino de escritura, el comando es
/// <see cref="IComandoDeAprovisionamiento"/>, el tenant coincide— y si no se
/// cumplen todas, simplemente no eleva y deja correr <c>next</c> con el rol
/// que ya tuviera (que para cualquier request normal es el que resuelve sin
/// sesión privilegiada, y para una sesión sin camino de escritura ya fue
/// denegada más afuera y este código ni se alcanza). El coste de repetir la
/// comprobación es memo pura —<c>RevalidarAsync</c> ya dejó su resultado como
/// memo de este ámbito de DI, así que esta segunda llamada no vuelve a tocar
/// la base—, y es más barato y más legible que inventar un canal para pasar
/// la decisión de un behavior a otro.
/// </summary>
public class ElevacionEscrituraAprovisionamientoBehavior<TRequest, TResponse>(
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ITenantActual tenantActual,
    IElevacionEscrituraPrivilegiada elevacionEscrituraPrivilegiada)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not IComandoDeAprovisionamiento)
            return await next(cancellationToken);

        var sesion = await sesionPrivilegiadaActual.RevalidarAsync(cancellationToken);

        if (sesion is not { TieneCaminoDeEscritura: true } s || tenantActual.TenantId != s.TenantObjetivoId)
            return await next(cancellationToken);

        await using var ambito = await elevacionEscrituraPrivilegiada.EstablecerAsync(
            s.SesionId, s.TenantObjetivoId, cancellationToken);

        return await next(cancellationToken);
    }
}
