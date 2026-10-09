using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
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
/// filtro global ni RLS de Tenant; el Tenant va en la clave que compone
/// Application— solo los ve y borra su propio usuario, no cualquiera dentro del
/// mismo Tenant, y solo dentro del Tenant activo en el que los guardó.
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
            var uno = Empresa.CrearComoCliente("Uno S.A.", "B12345674", false, null, null);
            var dos = Empresa.CrearComoCliente("Dos S.A.", "B87654323", false, null, null);
            contexto.Empresas.AddRange(uno, dos);
            await contexto.SaveChangesAsync();
            unoId = uno.Id;
            dosId = dos.Id;
        }

        await using (var contexto = CrearContexto())
        {
            var handler = new EliminarClientesCommandHandler(
                new EmpresaRepository(contexto), new AlcanceDatosServiceFalso(), contexto, new CurrentUserServiceFalso(Guid.NewGuid()));
            var resultado = await handler.Handle(new EliminarClientesCommand([unoId, dosId]), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
            resultado.Valor.Eliminados.Should().Be(2);
        }

        await using var verificacion = CrearContexto();
        (await verificacion.Empresas.CountAsync(c => c.Id == unoId || c.Id == dosId)).Should().Be(0,
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
                new CurrentUserServiceFalso(usuarioA), TenantActivo(_tenantId), new FiltroGuardadoRepository(contexto), contexto);
            var resultado = await handlerGuardar.Handle(
                new GuardarFiltroCommand(PantallasConFiltrosGuardados.Clientes, "Filtro de A", "{}"), CancellationToken.None);
            filtroDeAId = resultado.Valor;
        }

        await using (var contextoConsulta = CrearContexto())
        {
            var handlerObtener = new ObtenerFiltrosGuardadosQueryHandler(
                contextoConsulta, new CurrentUserServiceFalso(usuarioB), TenantActivo(_tenantId));
            var filtrosDeB = await handlerObtener.Handle(
                new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Clientes), CancellationToken.None);

            filtrosDeB.Should().BeEmpty("el filtro pertenece a otro usuario");
        }

        await using (var contextoBorrar = CrearContexto())
        {
            var handlerEliminar = new EliminarFiltroGuardadoCommandHandler(
                new CurrentUserServiceFalso(usuarioB), TenantActivo(_tenantId), new FiltroGuardadoRepository(contextoBorrar), contextoBorrar);
            var resultado = await handlerEliminar.Handle(new EliminarFiltroGuardadoCommand(filtroDeAId), CancellationToken.None);

            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be("FiltroGuardado.NoEncontrado");
        }

        await using var verificacion = CrearContexto();
        (await verificacion.FiltrosGuardados.CountAsync(f => f.Id == filtroDeAId)).Should().Be(1, "sigue existiendo, no lo pudo borrar B");
    }

    /// <summary>
    /// El caso del Gestor CAE de un Operador CAE externo: UN usuario, cartera en
    /// dos Tenant. La tabla no tiene filtro global ni RLS de Tenant, así que el
    /// contexto de EF la lee entera sea cual sea el Tenant activo — lo único que
    /// separa los filtros es la clave. Se prueba con «Trabajadores», la pantalla
    /// de nombre más largo, contra el <c>varchar(50)</c> real: que la clave
    /// quepa de verdad solo lo puede decir Postgres.
    /// </summary>
    [Fact]
    public async Task El_mismo_usuario_no_ve_ni_puede_borrar_en_un_Tenant_el_filtro_que_guardo_en_otro()
    {
        var usuario = Guid.NewGuid();
        var otroTenantId = Guid.NewGuid();
        Guid filtroId;

        await using (var contexto = CrearContexto())
        {
            // Fila anterior a la regla: nombre de la pantalla a secas.
            contexto.FiltrosGuardados.Add(new FiltroGuardado(usuario, PantallasConFiltrosGuardados.Trabajadores, "Antiguo sin Tenant", "{}"));
            await contexto.SaveChangesAsync();

            var resultado = await new GuardarFiltroCommandHandler(
                    new CurrentUserServiceFalso(usuario), TenantActivo(_tenantId), new FiltroGuardadoRepository(contexto), contexto)
                .Handle(new GuardarFiltroCommand(PantallasConFiltrosGuardados.Trabajadores, "Del primer Tenant", "{}"), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue("la clave compuesta de la pantalla más larga cabe en la columna real");
            filtroId = resultado.Valor;
        }

        await using (var contextoConsulta = CrearContexto())
        {
            var enElMismoTenant = await new ObtenerFiltrosGuardadosQueryHandler(
                    contextoConsulta, new CurrentUserServiceFalso(usuario), TenantActivo(_tenantId))
                .Handle(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Trabajadores), CancellationToken.None);
            var enElOtroTenant = await new ObtenerFiltrosGuardadosQueryHandler(
                    contextoConsulta, new CurrentUserServiceFalso(usuario), TenantActivo(otroTenantId))
                .Handle(new ObtenerFiltrosGuardadosQuery(PantallasConFiltrosGuardados.Trabajadores), CancellationToken.None);

            enElMismoTenant.Select(f => f.Nombre).Should().Equal(["Del primer Tenant"], "ni la fila antigua sin Tenant ni nada más");
            enElOtroTenant.Should().BeEmpty("el filtro lleva identificadores del primer Tenant");
        }

        await using (var contextoBorrar = CrearContexto())
        {
            var resultado = await new EliminarFiltroGuardadoCommandHandler(
                    new CurrentUserServiceFalso(usuario), TenantActivo(otroTenantId), new FiltroGuardadoRepository(contextoBorrar), contextoBorrar)
                .Handle(new EliminarFiltroGuardadoCommand(filtroId), CancellationToken.None);

            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be("FiltroGuardado.NoEncontrado");
        }

        await using var verificacion = CrearContexto();
        (await verificacion.FiltrosGuardados.CountAsync(f => f.UsuarioId == usuario)).Should().Be(2, "ni el filtro del primer Tenant ni la fila antigua se han borrado");
    }

    private static TenantActualAmbiental TenantActivo(Guid tenantId) => new() { TenantId = tenantId };
}
