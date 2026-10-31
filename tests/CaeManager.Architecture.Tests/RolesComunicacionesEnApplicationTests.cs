using CaeManager.Application.Comunicaciones.Queries.ObtenerAdjuntoParaDescarga;
using CaeManager.Web.Features.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Quien entra en la cola de triage lo dice <see cref="RolesComunicaciones.Gestion"/>, en Web, y quien se lleva sus
/// adjuntos lo dice la consulta de Application, que no alcanza esa constante. Sin este test, ampliar uno de los dos
/// dejaría un enlace que da 404, o un adjunto que se descarga por fuera de la pantalla.
/// </summary>
public class RolesComunicacionesEnApplicationTests
{
    [Fact]
    public void Los_roles_de_triage_de_la_consulta_de_adjuntos_son_los_de_la_pantalla()
    {
        ObtenerAdjuntoParaDescargaQueryHandler.RolesConAccesoATriage
            .Should().BeEquivalentTo(RolesComunicaciones.Gestion.Split(','));
    }
}
