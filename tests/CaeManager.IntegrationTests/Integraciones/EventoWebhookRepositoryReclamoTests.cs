using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// Igual que <c>TrabajoAnalisisDocumentoRepositoryReclamoTests</c> pero para
/// la cola de <see cref="EventoWebhook"/> (auditoría de colas, 2026-08-30) —
/// mismo reclamo atómico con <c>FOR UPDATE SKIP LOCKED</c>, con el filtro por
/// proveedor añadido (cada consumidor, Microsoft 365 o WhatsApp, drena solo
/// su propia cola).
/// </summary>
public class EventoWebhookRepositoryReclamoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto(_tenantA);
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            // EnableRetryOnFailure como en ConfiguracionDeContexto (producción)
            // — ver TrabajoAnalisisDocumentoRepositoryReclamoTests para el
            // porqué: sin esto el test no detecta que una transacción abierta
            // a mano revienta bajo NpgsqlRetryingExecutionStrategy.
            .UseNpgsql(_cadenaConexion, npgsql =>
            {
                npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL");
                npgsql.EnableRetryOnFailure();
            })
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task ReclamarSiguientePendienteAsync_lo_marca_procesando_y_respeta_el_proveedor()
    {
        Guid conexionM365Id, conexionWhatsAppId;
        await using (var contexto = CrearContexto(_tenantA))
        {
            var conexionM365 = new ConexionIntegracion("buzon@ejemplo.com", "Buzón M365");
            var conexionWhatsApp = new ConexionIntegracion("+34600000000", "Línea WhatsApp", proveedor: ProveedorIntegracion.WhatsApp);
            contexto.ConexionesIntegracion.AddRange(conexionM365, conexionWhatsApp);
            await contexto.SaveChangesAsync();
            conexionM365Id = conexionM365.Id;
            conexionWhatsAppId = conexionWhatsApp.Id;

            new EventoWebhookRepository(contexto).Agregar(new EventoWebhook(conexionWhatsAppId, "{\"whatsapp\":true}"));
            await contexto.SaveChangesAsync();
        }

        await using var contextoM365 = CrearContexto(_tenantA);
        (await new EventoWebhookRepository(contextoM365).ReclamarSiguientePendienteAsync(ProveedorIntegracion.Microsoft365))
            .Should().BeNull("el único evento pendiente pertenece a la conexión de WhatsApp, no a Microsoft 365");

        await using var contextoWhatsApp = CrearContexto(_tenantA);
        var reclamado = await new EventoWebhookRepository(contextoWhatsApp).ReclamarSiguientePendienteAsync(ProveedorIntegracion.WhatsApp);

        reclamado.Should().NotBeNull();
        reclamado!.ConexionIntegracionId.Should().Be(conexionWhatsAppId);
        reclamado.Estado.Should().Be(EstadoEventoWebhook.Procesando);
    }

    [Fact]
    public async Task ReclamarSiguientePendienteAsync_no_reclama_uno_todavia_en_backoff()
    {
        await using (var contexto = CrearContexto(_tenantA))
        {
            var conexion = new ConexionIntegracion("buzon@ejemplo.com", "Buzón M365");
            contexto.ConexionesIntegracion.Add(conexion);
            await contexto.SaveChangesAsync();

            var evento = new EventoWebhook(conexion.Id, "{}");
            evento.RegistrarFallo("fallo transitorio simulado");
            new EventoWebhookRepository(contexto).Agregar(evento);
            await contexto.SaveChangesAsync();
        }

        await using var contexto2 = CrearContexto(_tenantA);
        var reclamado = await new EventoWebhookRepository(contexto2).ReclamarSiguientePendienteAsync(ProveedorIntegracion.Microsoft365);

        reclamado.Should().BeNull("SiguienteIntentoEnUtc todavía está en el futuro tras el backoff");
    }

    [Fact]
    public async Task ObtenerEstancadosAsync_devuelve_los_que_llevan_procesando_mas_del_umbral()
    {
        await using var contexto = CrearContexto(_tenantA);
        var conexion = new ConexionIntegracion("buzon@ejemplo.com", "Buzón M365");
        contexto.ConexionesIntegracion.Add(conexion);
        await contexto.SaveChangesAsync();

        var repositorio = new EventoWebhookRepository(contexto);

        var estancado = new EventoWebhook(conexion.Id, "{}");
        estancado.MarcarEnProceso();
        repositorio.Agregar(estancado);

        var pendiente = new EventoWebhook(conexion.Id, "{}");
        repositorio.Agregar(pendiente);

        await contexto.SaveChangesAsync();

        var estancados = await repositorio.ObtenerEstancadosAsync(ProveedorIntegracion.Microsoft365, TimeSpan.Zero);

        estancados.Should().ContainSingle().Which.Id.Should().Be(estancado.Id);
    }
}
