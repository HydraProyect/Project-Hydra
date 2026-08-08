using CaeManager.Application.Cumplimiento.Commands.AceptarTerminos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Cumplimiento;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Cumplimiento;

public class AceptarTerminosCommandHandlerTests
{
    [Fact]
    public async Task Registra_la_aceptacion_de_la_version_vigente()
    {
        var usuarioId = Guid.NewGuid();
        var repositorio = new AceptacionTerminosRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new AceptarTerminosCommandHandler(repositorio, new CurrentUserServiceFalso(usuarioId), unitOfWork);

        var resultado = await handler.Handle(new AceptarTerminosCommand(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        repositorio.Aceptaciones.Should().ContainSingle(a => a.UsuarioId == usuarioId && a.VersionDocumento == VersionTerminos.Actual);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Es_idempotente_si_ya_habia_aceptado_esa_version()
    {
        var usuarioId = Guid.NewGuid();
        var repositorio = new AceptacionTerminosRepositorioFalso();
        repositorio.Agregar(new AceptacionTerminos(usuarioId, VersionTerminos.Actual, DateTime.UtcNow));
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new AceptarTerminosCommandHandler(repositorio, new CurrentUserServiceFalso(usuarioId), unitOfWork);

        var resultado = await handler.Handle(new AceptarTerminosCommand(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        repositorio.Aceptaciones.Should().ContainSingle("no debe duplicar la aceptación de la misma versión");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Falla_sin_usuario_identificado()
    {
        var repositorio = new AceptacionTerminosRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new AceptarTerminosCommandHandler(repositorio, new CurrentUserServiceFalso(usuarioId: null), unitOfWork);

        var resultado = await handler.Handle(new AceptarTerminosCommand(), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("AceptacionTerminos.SinUsuario");
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
