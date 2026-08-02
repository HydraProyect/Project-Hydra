using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Domain.Clientes;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.KitUsuarioExperto;

/// <summary>
/// P3-31, contra PostgreSQL real: el borrado en lote hace una única
/// transacción de verdad (no per-fila), y los filtros guardados —Entity sin
/// filtro de tenant, mismo tratamiento que PreferenciaDashboardUsuario— solo
/// los ve y borra su propio usuario, no cualquiera dentro del mismo tenant.
/// </summary>
public class BorradoEnLoteYFiltrosGuardadosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task Elimina_en_lote_dentro_de_una_sola_transaccion()
    {
        Guid unoId, dosId;
        await using (var contexto = CrearContexto())
        {
            var uno = new Cliente("Uno S.A.", "B12345674", false);
            var dos = new Cliente("Dos S.A.", "B87654323", false);
            contexto.Clientes.AddRange(uno, dos);
            await contexto.SaveChangesAsync();
            unoId = uno.Id;
            dosId = dos.Id;
        }

        await using (var contexto = CrearContexto())
        {
            var handler = new EliminarClientesCommandHandler(new ClienteRepository(contexto), new AlcanceDatosServiceFalso(), contexto);
            var resultado = await handler.Handle(new EliminarClientesCommand([unoId, dosId], Guid.NewGuid()), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
            resultado.Valor.Eliminados.Should().Be(2);
        }

        await using var verificacion = CrearContexto();
        (await verificacion.Clientes.CountAsync(c => c.Id == unoId || c.Id == dosId)).Should().Be(0,
            "el filtro global de soft delete oculta ambos tras eliminarlos");
    }

    [Fact]
    public async Task Un_usuario_no_ve_ni_puede_borrar_el_filtro_guardado_de_otro()
    {
        var usuarioA = Guid.NewGuid();
        var usuarioB = Guid.NewGuid();
        Guid filtroDeAId;

        await using (var contexto = CrearContexto())
        {
            var handlerGuardar = new GuardarFiltroCommandHandler(
                new CurrentUserServiceFalso(usuarioA), new FiltroGuardadoRepository(contexto), contexto);
            var resultado = await handlerGuardar.Handle(
                new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, "Filtro de A", "{}"), CancellationToken.None);
            filtroDeAId = resultado.Valor;
        }

        await using (var contextoConsulta = CrearContexto())
        {
            var handlerObtener = new ObtenerFiltrosGuardadosQueryHandler(contextoConsulta, new CurrentUserServiceFalso(usuarioB));
            var filtrosDeB = await handlerObtener.Handle(
                new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Clientes), CancellationToken.None);

            filtrosDeB.Should().BeEmpty("el filtro pertenece a otro usuario");
        }

        await using (var contextoBorrar = CrearContexto())
        {
            var handlerEliminar = new EliminarFiltroGuardadoCommandHandler(
                new CurrentUserServiceFalso(usuarioB), new FiltroGuardadoRepository(contextoBorrar), contextoBorrar);
            var resultado = await handlerEliminar.Handle(new EliminarFiltroGuardadoCommand(filtroDeAId), CancellationToken.None);

            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be("FiltroGuardado.NoEncontrado");
        }

        await using var verificacion = CrearContexto();
        (await verificacion.FiltrosGuardados.CountAsync(f => f.Id == filtroDeAId)).Should().Be(1, "sigue existiendo, no lo pudo borrar B");
    }
}
