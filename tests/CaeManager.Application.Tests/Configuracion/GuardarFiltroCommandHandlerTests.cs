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
        var handler = new GuardarFiltroCommandHandler(
            new CurrentUserServiceFalso(usuarioId), new TenantActualFijo(Guid.NewGuid()), repositorio, unitOfWork);

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
        var handler = new GuardarFiltroCommandHandler(
            new CurrentUserServiceFalso(), new TenantActualFijo(Guid.NewGuid()), repositorio, unitOfWork);

        var resultado = await handler.Handle(
            new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, "Críticos", "{}"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("FiltroGuardado.SinUsuario");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sin_Tenant_activo_no_se_guarda_y_el_fallo_es_legible(bool tenantVacio)
    {
        var repositorio = new FiltroGuardadoRepositorioFalso();
        var handler = new GuardarFiltroCommandHandler(
            new CurrentUserServiceFalso(Guid.NewGuid()), new TenantActualFijo(tenantVacio ? Guid.Empty : null),
            repositorio, new Clientes.UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, "Huérfano", "{}"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("FiltroGuardado.SinTenant");
        resultado.Error.Mensaje.Should().NotBeNullOrWhiteSpace();
        repositorio.Filtros.Should().BeEmpty();
    }

    [Fact]
    public async Task No_guarda_dos_filtros_con_el_mismo_nombre_en_la_misma_pantalla()
    {
        var usuarioId = Guid.NewGuid();
        var repositorio = new FiltroGuardadoRepositorioFalso();
        var handler = new GuardarFiltroCommandHandler(
            new CurrentUserServiceFalso(usuarioId), new TenantActualFijo(Guid.NewGuid()), repositorio, new Clientes.UnitOfWorkFalso());
        await handler.Handle(new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, "Críticos", "{}"), CancellationToken.None);

        var repetido = await handler.Handle(
            new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, " Críticos ", "{}"), CancellationToken.None);
        var enOtraPantalla = await handler.Handle(
            new GuardarFiltroCommand(PantallasConFiltrosGuardados.Documentos, "Críticos", "{}"), CancellationToken.None);

        repetido.EsFallido.Should().BeTrue();
        repetido.Error.Codigo.Should().Be("FiltroGuardado.NombreDuplicado");
        enOtraPantalla.EsExitoso.Should().BeTrue("el nombre solo es único dentro de su pantalla");
        repositorio.Filtros.Should().HaveCount(2);
    }

    [Fact]
    public async Task El_nombre_de_otro_usuario_no_bloquea_el_propio()
    {
        var repositorio = new FiltroGuardadoRepositorioFalso();
        var unitOfWork = new Clientes.UnitOfWorkFalso();
        var tenant = new TenantActualFijo(Guid.NewGuid());
        await new GuardarFiltroCommandHandler(new CurrentUserServiceFalso(Guid.NewGuid()), tenant, repositorio, unitOfWork)
            .Handle(new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, "Críticos", "{}"), CancellationToken.None);

        var resultado = await new GuardarFiltroCommandHandler(new CurrentUserServiceFalso(Guid.NewGuid()), tenant, repositorio, unitOfWork)
            .Handle(new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, "Críticos", "{}"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }
}
