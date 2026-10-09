using CaeManager.Application.Common;
using MediatR;

namespace CaeManager.Application.Usuarios.Queries.ObtenerDobleFactorPropio;

/// <summary>
/// Si la cuenta que mira tiene activa la autenticación en dos pasos. Sirve para AVISAR en una tarjeta de
/// credenciales («Activa la autenticación en dos pasos para verlas») sin pedir la credencial al abrir la ficha.
/// </summary>
/// <remarks>
/// No es autorización: quien decide si una credencial se entrega sigue siendo
/// <see cref="AutorizacionSecretosDeTenantBehavior"/>, en el momento de pedirla y con el mismo dato
/// (<see cref="ICurrentUserService.TieneDobleFactorActivoAsync"/>, leído en fresco). Una pantalla que se fíe de esta
/// respuesta y pida la credencial puede recibir igualmente
/// <see cref="SegundoFactorRequeridoParaCredencialesException"/> si el doble factor se restableció entre medias.
/// Va por la tubería de MediatR, y no leyendo el servicio desde el componente, para que la lectura de la cuenta se
/// serialice con el resto de accesos a datos del circuito.
/// </remarks>
public record ObtenerDobleFactorPropioQuery : IRequest<bool>;

public class ObtenerDobleFactorPropioQueryHandler(ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerDobleFactorPropioQuery, bool>
{
    public Task<bool> Handle(ObtenerDobleFactorPropioQuery request, CancellationToken cancellationToken) =>
        currentUserService.TieneDobleFactorActivoAsync();
}
