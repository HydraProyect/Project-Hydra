using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Configuracion;

public class GuardarFiltroCommandHandlerTests
{
    [Fact]
    public async Task Guarda_el_filtro_para_el_usuario_actual()
    {
        var usuarioId = Guid.NewGuid();
        var repositorio = new FiltroGuardadoRepositorioFalso();
        var unitOfWork = new Clientes.UnitOfWorkFalso();
        var handler = new GuardarFiltroCommandHandler(new CurrentUserServiceFalso(usuarioId), repositorio, unitOfWork);

        var resultado = await handler.Handle(
            new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, "Críticos", "{\"soloCriticos\":true}"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        repositorio.Filtros.Should().ContainSingle(f => f.UsuarioId == usuarioId && f.Nombre == "Críticos");
    }

    [Fact]
    public async Task Falla_sin_usuario_identificado()
    {
        var repositorio = new FiltroGuardadoRepositorioFalso();
        var unitOfWork = new Clientes.UnitOfWorkFalso();
        var handler = new GuardarFiltroCommandHandler(new CurrentUserServiceFalso(), repositorio, unitOfWork);

        var resultado = await handler.Handle(
            new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, "Críticos", "{}"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("FiltroGuardado.SinUsuario");
    }
}
