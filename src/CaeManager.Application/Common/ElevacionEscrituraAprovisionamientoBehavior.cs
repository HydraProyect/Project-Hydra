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
///
/// <b>Este behavior abre el <see cref="AmbitoEscrituraPrivilegiada"/> él
/// mismo, con una llamada SÍNCRONA</b> — nunca a través de un método
/// <c>async</c> de <see cref="IElevacionEscrituraPrivilegiada"/> (revisión 5,
/// Codex): <c>AmbitoEscrituraPrivilegiada.Establecer</c> muta un
/// <c>AsyncLocal</c>, y esa mutación solo sobrevive de vuelta en ESTE método
/// si ocurre directamente en su propio cuerpo. Delegarla a un método
/// <c>async</c> ajeno que completa de forma síncrona (el caso más común: la
/// conexión ya está cerrada) pierde la mutación en cuanto ese método
/// retorna, y el interceptor de la siguiente apertura de conexión no ve
/// ningún ámbito abierto — adopta <c>cae_app_soporte</c> en vez de
/// <c>cae_app_aprovisionamiento</c>, y la importación revienta con 42501.
/// <see cref="IElevacionEscrituraPrivilegiada"/> ahora solo mueve el rol de
/// Postgres; el ámbito lo abre y cierra este código.
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

        // Llamada síncrona, a propósito — ver el comentario de esta clase.
        var ambito = AmbitoEscrituraPrivilegiada.Establecer(s.SesionId, s.TenantObjetivoId);
        await elevacionEscrituraPrivilegiada.ElevarSiConexionAbiertaAsync(cancellationToken);
        try
        {
            return await next(cancellationToken);
        }
        finally
        {
            // Orden normativo: limpiar el AsyncLocal ANTES del SET ROLE de
            // vuelta, para que el caso habitual —SaveChangesAsync ya cerró la
            // conexión antes de llegar aquí— también quede seguro sin
            // depender de que el SET ROLE llegue a ejecutarse: sin ámbito
            // abierto, la próxima apertura cae en cae_app_soporte por la
            // rama normal del interceptor.
            ambito.Dispose();
            await elevacionEscrituraPrivilegiada.DevolverSiConexionAbiertaAsync(cancellationToken);
        }
    }
}
