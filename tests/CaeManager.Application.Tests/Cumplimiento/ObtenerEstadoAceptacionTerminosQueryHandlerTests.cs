using CaeManager.Application.Cumplimiento.Queries.ObtenerEstadoAceptacionTerminos;
using CaeManager.Domain.Cumplimiento;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Cumplimiento;

public class ObtenerEstadoAceptacionTerminosQueryHandlerTests
{
    [Fact]
    public async Task Devuelve_true_si_el_usuario_nunca_acepto()
    {
        var handler = new ObtenerEstadoAceptacionTerminosQueryHandler(
            new AceptacionTerminosRepositorioFalso(), new CurrentUserServiceFalso(Guid.NewGuid()));

        (await handler.Handle(new ObtenerEstadoAceptacionTerminosQuery(), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Devuelve_false_si_ya_acepto_la_version_vigente()
    {
        var usuarioId = Guid.NewGuid();
        var repositorio = new AceptacionTerminosRepositorioFalso();
        repositorio.Agregar(new AceptacionTerminos(usuarioId, VersionTerminos.Actual, DateTime.UtcNow));
        var handler = new ObtenerEstadoAceptacionTerminosQueryHandler(repositorio, new CurrentUserServiceFalso(usuarioId));

        (await handler.Handle(new ObtenerEstadoAceptacionTerminosQuery(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Devuelve_true_si_acepto_una_version_anterior()
    {
        var usuarioId = Guid.NewGuid();
        var repositorio = new AceptacionTerminosRepositorioFalso();
        repositorio.Agregar(new AceptacionTerminos(usuarioId, "2025-01-01", DateTime.UtcNow));
        var handler = new ObtenerEstadoAceptacionTerminosQueryHandler(repositorio, new CurrentUserServiceFalso(usuarioId));

        (await handler.Handle(new ObtenerEstadoAceptacionTerminosQuery(), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Devuelve_false_sin_usuario_identificado()
    {
        var handler = new ObtenerEstadoAceptacionTerminosQueryHandler(
            new AceptacionTerminosRepositorioFalso(), new CurrentUserServiceFalso(usuarioId: null));

        (await handler.Handle(new ObtenerEstadoAceptacionTerminosQuery(), CancellationToken.None)).Should().BeFalse();
    }
}
