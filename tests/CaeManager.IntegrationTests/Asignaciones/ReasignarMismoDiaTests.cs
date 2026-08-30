using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignacion;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Asignaciones;

/// <summary>
/// Regresión del bug real reproducido con datos de producción (Shin Nohara,
/// Terminal Ciudad Gotica 016): dar de baja una Asignacion y reasignar al
/// mismo trabajador al mismo centro el mismo día lanzaba 23505 de Postgres.
/// La causa era que el índice único de "Asignaciones" incluía FechaAlta —
/// la fila inactiva con esa fecha colisionaba con la nueva aunque
/// ExisteActivaAsync (FechaBaja == null) ya la descartara. El índice ahora es
/// único por (tenant, trabajador, centro) SOLO entre las activas.
/// </summary>
public class ReasignarMismoDiaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _trabajadorId;
    private Guid _centroId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Reasignación S.A.", "B12345674", false, null, null);
        var empresa = new Empresa("Contratas de Reasignación S.L.", "B87654323");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Terminal Ciudad Gótica 016");
        contexto.Centros.Add(centro);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Shin", "Nohara", "77189989B");
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        _trabajadorId = trabajador.Id;
        _centroId = centro.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Dar_de_baja_y_reasignar_el_mismo_dia_no_colisiona_con_el_indice_unico()
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        Guid asignacionOriginalId;
        await using (var contexto = CrearContexto())
        {
            var creacion = new CrearAsignacionCommandHandler(new AsignacionRepository(contexto), new AutoridadAsignacionesServiceFalso(contexto), contexto);
            var alta = await creacion.Handle(new CrearAsignacionCommand(_trabajadorId, _centroId, hoy), CancellationToken.None);
            alta.EsExitoso.Should().BeTrue();
            asignacionOriginalId = alta.Valor;
        }

        await using (var contextoBaja = CrearContexto())
        {
            var baja = new DarDeBajaAsignacionCommandHandler(new AsignacionRepository(contextoBaja), new AutoridadAsignacionesServiceFalso(contextoBaja), contextoBaja);
            var resultadoBaja = await baja.Handle(new DarDeBajaAsignacionCommand(asignacionOriginalId, hoy), CancellationToken.None);
            resultadoBaja.EsExitoso.Should().BeTrue();
        }

        await using (var contextoReasignar = CrearContexto())
        {
            var reasignacion = new CrearAsignacionCommandHandler(new AsignacionRepository(contextoReasignar), new AutoridadAsignacionesServiceFalso(contextoReasignar), contextoReasignar);
            var resultado = await reasignacion.Handle(new CrearAsignacionCommand(_trabajadorId, _centroId, hoy), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var verificacion = CrearContexto();
        var activas = await verificacion.Asignaciones
            .Where(a => a.TrabajadorId == _trabajadorId && a.CentroId == _centroId && a.FechaBaja == null)
            .CountAsync();
        activas.Should().Be(1);
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
