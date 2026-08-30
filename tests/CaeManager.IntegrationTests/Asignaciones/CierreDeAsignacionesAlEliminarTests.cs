using CaeManager.Application.Centros.Commands.EliminarCentro;
using CaeManager.Application.Trabajadores.Commands.EliminarTrabajador;
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
/// El cierre de asignaciones al borrar un Centro o un Trabajador,
/// <b>contra PostgreSQL de verdad</b>.
///
/// <para>
/// Los tests de Application prueban que el handler llama al cierre, pero lo
/// hacen contra un repositorio falso que <b>reimplementa</b> el filtro: si el
/// LINQ real perdiera el <c>FechaBaja == null</c> o filtrase por el centro
/// equivocado, aquéllos seguirían verdes. Esa propiedad —la consulta y la
/// persistencia— solo la puede demostrar esta capa (CLAUDE.md §4: ninguna
/// capa presta evidencia a otra).
/// </para>
/// </summary>
public class CierreDeAsignacionesAlEliminarTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _centroId;
    private Guid _otroCentroId;
    private Guid _trabajadorId;
    private Guid _otroTrabajadorId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Titular del Cierre S.A.", "B12345674", false, null, null);
        var empresa = new Empresa("Contratas del Cierre S.L.", "B87654323");
        contexto.Empresas.AddRange(cliente, empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro que se borra");
        var otroCentro = new Centro(cliente.Id, empresa.Id, "Centro que sigue vivo");
        contexto.Centros.AddRange(centro, otroCentro);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "77189989B");
        var otroTrabajador = Trabajador.DeEmpresa(empresa.Id, "Luis", "Pérez", "12345678Z");
        contexto.Trabajadores.AddRange(trabajador, otroTrabajador);
        await contexto.SaveChangesAsync();

        _centroId = centro.Id;
        _otroCentroId = otroCentro.Id;
        _trabajadorId = trabajador.Id;
        _otroTrabajadorId = otroTrabajador.Id;

        contexto.Asignaciones.AddRange(
            new Asignacion(_trabajadorId, _centroId, new DateOnly(2026, 1, 15)),
            new Asignacion(_otroTrabajadorId, _centroId, new DateOnly(2026, 1, 15)),
            new Asignacion(_trabajadorId, _otroCentroId, new DateOnly(2026, 1, 15)));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Borrar_el_centro_deja_sus_asignaciones_cerradas_en_la_base()
    {
        await using (var contexto = CrearContexto())
        {
            var handler = new EliminarCentroCommandHandler(
                new CentroRepository(contexto), new AsignacionRepository(contexto),
                new AlcanceDatosServiceFalso(), contexto);

            var resultado = await handler.Handle(
                new EliminarCentroCommand(_centroId, Guid.NewGuid()), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        // Contexto nuevo: se lee lo que quedó escrito, no el ChangeTracker.
        await using var verificacion = CrearContexto();

        var activasDelCentroBorrado = await verificacion.Asignaciones
            .Where(a => a.CentroId == _centroId && a.FechaBaja == null)
            .CountAsync();
        activasDelCentroBorrado.Should().Be(0, "el centro ya no existe y nadie puede seguir asignado a él");

        var activasDelOtroCentro = await verificacion.Asignaciones
            .Where(a => a.CentroId == _otroCentroId && a.FechaBaja == null)
            .CountAsync();
        activasDelOtroCentro.Should().Be(1, "el borrado de un centro no toca las asignaciones de otro");
    }

    [Fact]
    public async Task Borrar_el_trabajador_deja_sus_asignaciones_cerradas_en_la_base()
    {
        await using (var contexto = CrearContexto())
        {
            var handler = new EliminarTrabajadorCommandHandler(
                new TrabajadorRepository(contexto), new AsignacionRepository(contexto),
                new AlcanceDatosServiceFalso(), contexto);

            var resultado = await handler.Handle(
                new EliminarTrabajadorCommand(_trabajadorId, Guid.NewGuid()), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var verificacion = CrearContexto();

        var activasDelBorrado = await verificacion.Asignaciones
            .Where(a => a.TrabajadorId == _trabajadorId && a.FechaBaja == null)
            .CountAsync();
        activasDelBorrado.Should().Be(0, "sus dos asignaciones, en centros distintos, se cierran las dos");

        var activasDelOtro = await verificacion.Asignaciones
            .Where(a => a.TrabajadorId == _otroTrabajadorId && a.FechaBaja == null)
            .CountAsync();
        activasDelOtro.Should().Be(1, "el borrado de un trabajador no toca las asignaciones de otro");
    }

    [Fact]
    public async Task La_asignacion_cerrada_conserva_su_historial()
    {
        await using (var contexto = CrearContexto())
        {
            var handler = new EliminarCentroCommandHandler(
                new CentroRepository(contexto), new AsignacionRepository(contexto),
                new AlcanceDatosServiceFalso(), contexto);

            await handler.Handle(new EliminarCentroCommand(_centroId, Guid.NewGuid()), CancellationToken.None);
        }

        await using var verificacion = CrearContexto();

        // Cerrar no es borrar: la fila sigue ahí con su fecha de alta, que es
        // lo que Asignacion existe para conservar (ver DarDeBajaAsignacion).
        var cerradas = await verificacion.Asignaciones
            .Where(a => a.CentroId == _centroId)
            .ToListAsync();

        cerradas.Should().HaveCount(2);
        cerradas.Should().OnlyContain(a => a.FechaAlta == new DateOnly(2026, 1, 15));
        cerradas.Should().OnlyContain(a => a.FechaBaja != null);
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
