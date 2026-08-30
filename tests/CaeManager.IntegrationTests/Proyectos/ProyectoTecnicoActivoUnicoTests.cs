using CaeManager.Application.Proyectos.Commands.AsignarTecnicoProyecto;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Proyectos;

/// <summary>
/// Auditoría Módulo 5, hallazgo crítico 11/9: el índice único de
/// <c>ProyectosTecnicos</c> incluía <c>FechaAlta</c>, así que la carrera
/// SELECT-luego-INSERT de <c>AsignarTecnicoProyectoCommand</c> podía dejar
/// dos filas activas para el mismo proyecto-trabajador con fechas de alta
/// distintas — duplicidad operativa y documentación requerida generada dos
/// veces. Ahora el índice es único por (tenant, proyecto, trabajador) ENTRE
/// LOS ACTIVOS, contra PostgreSQL real.
/// </summary>
public class ProyectoTecnicoActivoUnicoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _clienteId;
    private Guid _proyectoId;
    private Guid _trabajadorId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Cliente Proyecto Técnico S.A.", "B12345674", false, null, null);
        var empresa = new Empresa("Contratas Técnicas S.L.", "B87654323");
        contexto.Empresas.AddRange(cliente, empresa);
        await contexto.SaveChangesAsync();

        var centro = new CaeManager.Domain.Centros.Centro(cliente.Id, empresa.Id, "Centro del Proyecto Técnico");
        contexto.Centros.Add(centro);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "77189989B");
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        var proyecto = Proyecto.Crear(cliente.Id, centro.Id, "Ampliación Planta 2", new DateOnly(2026, 1, 1), null, null);
        contexto.Proyectos.Add(proyecto);
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _proyectoId = proyecto.Id;
        _trabajadorId = trabajador.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Una_segunda_alta_activa_del_mismo_tecnico_con_otra_fecha_choca_en_la_base()
    {
        await using var contexto = CrearContexto();

        contexto.ProyectosTecnicos.Add(new ProyectoTecnico(_proyectoId, _trabajadorId, new DateOnly(2026, 1, 10)));
        await contexto.SaveChangesAsync();

        // Antes de la corrección, esto pasaba: FechaAlta distinta = clave
        // distinta, así que el índice viejo no lo detectaba.
        contexto.ProyectosTecnicos.Add(new ProyectoTecnico(_proyectoId, _trabajadorId, new DateOnly(2026, 2, 1)));

        await contexto.Invoking(c => c.SaveChangesAsync())
            .Should().ThrowAsync<DbUpdateException>("el índice único por activo impide una segunda alta con otra fecha");
    }

    [Fact]
    public async Task El_handler_traduce_el_choque_de_indice_en_un_resultado_legible()
    {
        // Simula la ventana de la carrera: una fila activa ya escrita por
        // "otra petición" justo antes de que el handler llegue a guardar.
        await using (var contextoPreparacion = CrearContexto())
        {
            contextoPreparacion.ProyectosTecnicos.Add(
                new ProyectoTecnico(_proyectoId, _trabajadorId, new DateOnly(2026, 1, 10)));
            await contextoPreparacion.SaveChangesAsync();
        }

        await using var contexto = CrearContexto();
        var handler = new AsignarTecnicoProyectoCommandHandler(
            new ProyectoTecnicoRepository(contexto), new ProyectoRepository(contexto), contexto,
            new AlcanceDatosServiceFalso(), contexto);

        var resultado = await handler.Handle(
            new AsignarTecnicoProyectoCommand(_proyectoId, _trabajadorId, new DateOnly(2026, 2, 1)),
            CancellationToken.None);

        // ExisteActivoAsync ya debería atraparlo primero (no es la carrera
        // real, pero confirma que el mensaje al usuario es el mismo,
        // sea cual sea la vía por la que se detecta).
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Proyecto.TecnicoYaAsignado");
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
