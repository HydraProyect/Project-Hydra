using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Domain.Configuracion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Configuracion;

public class EliminarFiltroGuardadoCommandHandlerTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private static readonly string ClaveDeClientes =
        PantallasConFiltrosGuardados.ClaveAlmacenada(PantallasConFiltrosGuardados.Clientes, TenantId)!;

    [Fact]
    public async Task El_dueno_puede_eliminar_su_filtro()
    {
        var usuarioId = Guid.NewGuid();
        var filtro = new FiltroGuardado(usuarioId, ClaveDeClientes, "Críticos", "{}");
        var repositorio = new FiltroGuardadoRepositorioFalso();
        repositorio.Agregar(filtro);
        var unitOfWork = new Clientes.UnitOfWorkFalso();
        var handler = new EliminarFiltroGuardadoCommandHandler(
            new CurrentUserServiceFalso(usuarioId), new TenantActualFijo(TenantId), repositorio, unitOfWork);

        var resultado = await handler.Handle(new EliminarFiltroGuardadoCommand(filtro.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        repositorio.Filtros.Should().BeEmpty();
    }

    /// <summary>
    /// No hay filtro de tenant ni de agregado que lo impida solo (FiltroGuardado
    /// es Entity, no EntidadConTenant) — la comprobación de dueño vive en el
    /// propio handler. Este test protege justo eso: el filtro es del MISMO
    /// Tenant activo, así que lo único que lo impide es el usuario. La otra
    /// mitad (mismo usuario, otro Tenant) está en FiltrosGuardadosPorTenantTests.
    /// </summary>
    [Fact]
    public async Task Un_usuario_no_puede_eliminar_el_filtro_de_otro()
    {
        var filtro = new FiltroGuardado(Guid.NewGuid(), ClaveDeClientes, "Críticos", "{}");
        var repositorio = new FiltroGuardadoRepositorioFalso();
        repositorio.Agregar(filtro);
        var unitOfWork = new Clientes.UnitOfWorkFalso();
        var handler = new EliminarFiltroGuardadoCommandHandler(
            new CurrentUserServiceFalso(Guid.NewGuid()), new TenantActualFijo(TenantId), repositorio, unitOfWork);

        var resultado = await handler.Handle(new EliminarFiltroGuardadoCommand(filtro.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("FiltroGuardado.NoEncontrado");
        repositorio.Filtros.Should().ContainSingle();
    }
}
