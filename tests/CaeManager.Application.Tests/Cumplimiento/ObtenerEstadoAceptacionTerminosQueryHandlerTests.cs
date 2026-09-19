using CaeManager.Application.Cumplimiento.Commands.AceptarTerminos;
using CaeManager.Application.Cumplimiento.Queries.ObtenerEstadoAceptacionTerminos;
using CaeManager.Application.Tests.Clientes;
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

    /// <summary>
    /// «2026-08-08» es la versión que llevaba el texto «solo EEE» (PoliticaPrivacidad § 3) y que ya aceptaron
    /// los usuarios de producción. Al cambiar ese texto (P24) la versión vigente debe ser otra: quien aceptó la
    /// anterior vuelve a ver el modal, y al aceptar la nueva deja de verlo.
    /// </summary>
    [Fact]
    public async Task Quien_acepto_la_version_con_solo_EEE_vuelve_a_aceptar_y_la_nueva_queda_registrada()
    {
        const string versionConSoloEee = "2026-08-08";
        VersionTerminos.Actual.Should().NotBe(versionConSoloEee);

        var usuarioId = Guid.NewGuid();
        var repositorio = new AceptacionTerminosRepositorioFalso();
        repositorio.Agregar(new AceptacionTerminos(usuarioId, versionConSoloEee, DateTime.UtcNow));
        var usuarioActual = new CurrentUserServiceFalso(usuarioId);
        var consulta = new ObtenerEstadoAceptacionTerminosQueryHandler(repositorio, usuarioActual);

        (await consulta.Handle(new ObtenerEstadoAceptacionTerminosQuery(), CancellationToken.None))
            .Should().BeTrue("la aceptación de la versión anterior no cubre el texto nuevo");

        var acepta = new AceptarTerminosCommandHandler(repositorio, usuarioActual, new UnitOfWorkFalso());
        (await acepta.Handle(new AceptarTerminosCommand(), CancellationToken.None)).EsExitoso.Should().BeTrue();

        repositorio.Aceptaciones.Should().Contain(a => a.UsuarioId == usuarioId && a.VersionDocumento == VersionTerminos.Actual);
        repositorio.Aceptaciones.Should().Contain(a => a.UsuarioId == usuarioId && a.VersionDocumento == versionConSoloEee,
            "la aceptación anterior se conserva como rastro");
        (await consulta.Handle(new ObtenerEstadoAceptacionTerminosQuery(), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Devuelve_false_sin_usuario_identificado()
    {
        var handler = new ObtenerEstadoAceptacionTerminosQueryHandler(
            new AceptacionTerminosRepositorioFalso(), new CurrentUserServiceFalso(usuarioId: null));

        (await handler.Handle(new ObtenerEstadoAceptacionTerminosQuery(), CancellationToken.None)).Should().BeFalse();
    }
}
