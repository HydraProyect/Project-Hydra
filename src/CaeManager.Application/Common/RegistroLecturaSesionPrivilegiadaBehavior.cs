using CaeManager.Application.Plataforma;
using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Pipeline behavior de MediatR (issue #2001): un request marcado
/// <see cref="ILecturaRegistradaEnSesionPrivilegiada"/> que corre bajo una Sesión Privilegiada
/// registra la lectura en <c>RegistrosActividadSoporte</c> ANTES de que corra el handler, y si
/// la fila no se escribe lanza <see cref="LecturaPrivilegiadaNoRegistradaException"/>: el
/// handler no lee nada y no se sirve nada (fallo cerrado).
///
/// <b>Registrado justo antes de <see cref="ElevacionEscrituraAprovisionamientoBehavior{TRequest,TResponse}"/></b>,
/// a propósito: la función <c>app_registrar_actividad_soporte_por_sesion</c> solo la ejecuta
/// <c>cae_app_soporte</c>, que es el rol de toda conexión de la sesión fuera del ámbito de
/// elevación. Dentro de ese ámbito (los comandos del Importador son
/// <see cref="IComandoDeAprovisionamiento"/>) la conexión es <c>cae_app_aprovisionamiento</c> y
/// la función respondería 42501. Va después de las autorizaciones: un request denegado no llega
/// a leer y no se registra.
///
/// Sin sesión resuelta no hay nada que registrar y el request sigue igual. Usa
/// <see cref="ISesionPrivilegiadaActual.ObtenerAsync"/>: la vigencia la vuelve a comprobar la
/// función en la base, de modo que una memo que siga nombrando una sesión ya cerrada o caducada
/// no deja pasar la lectura — la función la rechaza y este behavior falla en cerrado.
/// </summary>
public class RegistroLecturaSesionPrivilegiadaBehavior<TRequest, TResponse>(
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IRegistroLecturaSesionPrivilegiada registroLectura)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (request is not ILecturaRegistradaEnSesionPrivilegiada)
            return await next(cancellationToken);

        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not { } sesion)
            return await next(cancellationToken);

        // El detalle nombra la operación, nunca los datos leídos (RegistroActividadSoporte:
        // este registro es también dato personal del empleado de soporte).
        var operacion = typeof(TRequest).Name;
        if (!await registroLectura.RegistrarAsync(sesion.SesionId, operacion, cancellationToken))
            throw new LecturaPrivilegiadaNoRegistradaException(operacion);

        return await next(cancellationToken);
    }
}
