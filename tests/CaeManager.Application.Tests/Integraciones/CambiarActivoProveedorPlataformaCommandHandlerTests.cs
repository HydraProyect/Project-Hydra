using CaeManager.Application.Integraciones.Commands.CambiarActivoProveedorPlataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// El *kill switch* remoto de MVP2 (ARQUITECTURA-INTEGRACIONES.md § 14.5, en
/// el repositorio de negocio): antes de este Command, <c>Activar()</c>/
/// <c>Desactivar()</c> existían en <see cref="ProveedorPlataformaCae"/> pero
/// nada los disparaba — el catálogo solo se sembraba activo/inactivo, sin
/// forma de cambiarlo después de arrancar.
/// </summary>
public class CambiarActivoProveedorPlataformaCommandHandlerTests
{
    [Fact]
    public async Task Desactiva_el_proveedor_con_autorizacion_global()
    {
        var proveedor = new ProveedorPlataformaCae("dokify", "Dokify");
        var repositorio = new ProveedorPlataformaCaeRepositorioFalso();
        repositorio.Agregar(proveedor);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CambiarActivoProveedorPlataformaCommandHandler(
            repositorio, AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid()), unitOfWork);

        var resultado = await handler.Handle(
            new CambiarActivoProveedorPlataformaCommand(proveedor.Id, Activo: false), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        proveedor.Activo.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Reactiva_el_proveedor()
    {
        var proveedor = new ProveedorPlataformaCae("dokify", "Dokify", activo: false);
        var repositorio = new ProveedorPlataformaCaeRepositorioFalso();
        repositorio.Agregar(proveedor);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CambiarActivoProveedorPlataformaCommandHandler(
            repositorio, AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid()), unitOfWork);

        var resultado = await handler.Handle(
            new CambiarActivoProveedorPlataformaCommand(proveedor.Id, Activo: true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        proveedor.Activo.Should().BeTrue();
    }

    [Fact]
    public async Task Falla_sin_autorizacion_global()
    {
        // AcotadaA: tiene AdminPlataforma sobre un tenant concreto, pero el
        // catálogo de proveedores es transversal — nunca basta una concesión
        // acotada (mismo criterio que CrearClienteDeleganteCommand).
        var proveedor = new ProveedorPlataformaCae("dokify", "Dokify");
        var repositorio = new ProveedorPlataformaCaeRepositorioFalso();
        repositorio.Agregar(proveedor);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CambiarActivoProveedorPlataformaCommandHandler(
            repositorio, AutorizacionAdminPlataformaFalsa.AcotadaA(Guid.NewGuid()), new CurrentUserServiceFalso(Guid.NewGuid()), unitOfWork);

        var resultado = await handler.Handle(
            new CambiarActivoProveedorPlataformaCommand(proveedor.Id, Activo: false), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ProveedorPlataforma.SinPermiso");
        proveedor.Activo.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Falla_cuando_el_proveedor_no_existe()
    {
        var repositorio = new ProveedorPlataformaCaeRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CambiarActivoProveedorPlataformaCommandHandler(
            repositorio, AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid()), unitOfWork);

        var resultado = await handler.Handle(
            new CambiarActivoProveedorPlataformaCommand(Guid.NewGuid(), Activo: false), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ProveedorPlataforma.NoEncontrado");
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}
