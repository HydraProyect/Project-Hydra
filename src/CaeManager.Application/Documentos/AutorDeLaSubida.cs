using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos;

/// <summary>
/// La única regla que decide quién sube una versión de un Documento (D-12). Sale de la identidad de auditoría
/// (<see cref="IActorAuditoria"/>), nunca de la de autorización: el usuario efectivo es el Usuario simulado si hay
/// impersonación y el Actor real en otro caso, y el Actor real es siempre quien está detrás del teclado. Es el mismo
/// reparto que <c>RegistroAuditoria</c>.
///
/// <para>
/// Solo una persona es autora. Un proceso de sistema, una integración externa (una <c>ClaveApi</c> llega con
/// identidad resuelta, pero no es una persona) o una identidad sin resolver dejan la versión sin autor, que cae en
/// D-12.1.
/// </para>
///
/// <para>
/// Una persona con un Id vacío no pasa a «sin autor», que es el estado que se puede validar: es una identidad
/// corrupta y la subida falla, porque <see cref="AutorVersionDocumento.Persona"/> lanza.
/// </para>
/// </summary>
public static class AutorDeLaSubida
{
    public static async Task<AutorVersionDocumento> ResolverAsync(IActorAuditoria actorAuditoria)
    {
        ArgumentNullException.ThrowIfNull(actorAuditoria);
        return Desde(await actorAuditoria.ObtenerAsync());
    }

    public static AutorVersionDocumento Desde(ActorAuditoria actor)
    {
        if (actor.ResolverTipoActor() != TipoActor.Persona || actor.ActorRealUsuarioId is not { } actorReal)
            return AutorVersionDocumento.Desconocido;

        return AutorVersionDocumento.Persona(actor.UsuarioSimuladoId ?? actorReal, actorReal);
    }
}
