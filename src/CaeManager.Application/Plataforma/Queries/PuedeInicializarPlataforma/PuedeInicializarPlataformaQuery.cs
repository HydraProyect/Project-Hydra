using CaeManager.Application.Common;
using MediatR;

namespace CaeManager.Application.Plataforma.Queries.PuedeInicializarPlataforma;

/// <summary>
/// ¿Debe mostrarse la puerta del acto fundacional a este usuario?
///
/// <para>
/// <b>Es un lector, no una política.</b> Responde "¿enseño el botón?", no
/// "¿puede ejecutarlo?". Esa distinción no es formal: la autoridad final es
/// siempre <c>AutoConcederPrivilegioCommand</c>, que vuelve a comprobar la
/// condición y además tiene el token de concurrencia del estado.
/// </para>
///
/// <para>
/// <b>Por eso una carrera aquí no es un bypass.</b> Esta consulta puede quedarse
/// desfasada y mostrar la puerta un instante después de que otro proceso haya
/// consumido el bootstrap; el comando denegará igual. Lo contrario —que la
/// consulta autorizase— sí sería un segundo camino de autoridad.
/// </para>
///
/// <para>
/// <b>Misma fuente de verdad, no una comparación paralela.</b> Delega en
/// <see cref="IRaizBootstrapPlataforma"/>, que es exactamente el predicado
/// "eres la raíz designada ∧ el bootstrap sigue disponible". Reimplementarlo
/// aquí —comparando contra el usuario, o peor, contra <c>EsPlataforma</c>, el
/// tenant actual o el email de configuración— produciría el mismo defecto que
/// evitamos en <c>EsAdministradorPlataformaQuery</c>: una superficie que dice
/// una cosa y un comando que dice otra.
/// </para>
///
/// <para>
/// Y esto <b>no</b> es <c>AdminPlataforma</c>. Ver la puerta es una propiedad
/// exclusiva de la identidad raíz mientras el bootstrap está pendiente; tener la
/// capacidad es lo que se obtiene <i>después</i> de cruzarla.
/// </para>
/// </summary>
public record PuedeInicializarPlataformaQuery : IRequest<bool>;

public class PuedeInicializarPlataformaQueryHandler(
    IRaizBootstrapPlataforma raizBootstrap,
    ICurrentUserService currentUserService)
    : IRequestHandler<PuedeInicializarPlataformaQuery, bool>
{
    public async Task<bool> Handle(PuedeInicializarPlataformaQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return false;

        return await raizBootstrap.EsRaizDeConfianzaAsync(usuarioId.Value, cancellationToken);
    }
}
