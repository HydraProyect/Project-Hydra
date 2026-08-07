using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.Visitas.Eventos;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>docs/COMUNICACIONES.md § 12.3/§ 16.7 — el evento de dominio se traduce en una entrada del timeline.</summary>
public class RegistrarEventoVisitaCreadaHandlerTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Registra_el_evento_de_visita_creada_en_la_conversacion()
    {
        var conversacionId = Guid.NewGuid();
        var visitaId = Guid.NewGuid();

        await using (var contexto = CrearContexto())
        {
            var handler = new RegistrarEventoVisitaCreadaHandler(
                new EventoConversacionRepository(contexto), contexto, NullLogger<RegistrarEventoVisitaCreadaHandler>.Instance);

            await handler.Handle(new VisitaCreadaEvent(conversacionId, visitaId), CancellationToken.None);
        }

        await using var lectura = CrearContexto();
        var evento = await lectura.EventosConversacion.SingleAsync(e => e.ConversacionId == conversacionId);

        evento.Tipo.Should().Be(TipoEventoConversacion.VisitaCreada);
        evento.ReferenciaId.Should().Be(visitaId);
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
