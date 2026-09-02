using CaeManager.Application.Plataforma.Queries.ObtenerSesionPrivilegiadaPorId;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// La búsqueda puntual que alimenta "observación con TTL" (H-2, plan de
/// sesiones nocturnas 2026-09-02, DEC-2) en la pantalla de cierre a la que
/// lleva <c>/cuenta/soporte/salir</c>. Ver el comentario normativo de
/// <see cref="ObtenerSesionPrivilegiadaPorIdQuery"/> sobre por qué esto no es
/// el "listar sesiones" que el contrato de lectura del plano 3 prohíbe.
/// </summary>
public class ObtenerSesionPrivilegiadaPorIdTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tecnico = Guid.NewGuid();
    private readonly Guid _tenantVisitado = Guid.NewGuid();

    private Guid _sesionId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, CapacidadPrivilegio.BreakGlass, [_tenantVisitado],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));
        var sesion = SesionPrivilegiada.Abrir(
            concesion, _tenantVisitado, "Corregir un dato incorrecto a petición del cliente",
            ahora.AddMinutes(-9), TimeSpan.FromHours(2), ticket: "TCK-123");

        contexto.ConcesionesPrivilegio.Add(concesion);
        contexto.SesionesPrivilegiadas.Add(sesion);
        await contexto.SaveChangesAsync();

        _sesionId = sesion.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Trae_el_detalle_completo_de_la_sesion_que_nombra_el_id()
    {
        var detalle = await EjecutarAsync(_sesionId);

        detalle.Should().NotBeNull();
        detalle!.TenantObjetivoId.Should().Be(_tenantVisitado);
        detalle.Capacidad.Should().Be(CapacidadPrivilegio.BreakGlass);
        detalle.Motivo.Should().Be("Corregir un dato incorrecto a petición del cliente");
        detalle.Ticket.Should().Be("TCK-123");
    }

    [Fact]
    public async Task Un_id_que_no_existe_devuelve_null_no_una_excepcion()
    {
        var detalle = await EjecutarAsync(Guid.NewGuid());

        detalle.Should().BeNull();
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<SesionPrivilegiadaDetalleDto?> EjecutarAsync(Guid sesionId)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerSesionPrivilegiadaPorIdQueryHandler(contexto);

        return await handler.Handle(new ObtenerSesionPrivilegiadaPorIdQuery(sesionId), CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantVisitado };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
