using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>docs/COMUNICACIONES.md § 12.3/§ 16.7 — eventos del sistema mezclados con los mensajes del hilo.</summary>
public class ObtenerConversacionPorIdQueryEventosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly AlcanceDatosServiceFalso _alcanceDatos = new();
    private readonly CurrentUserServiceFalso _currentUser = new(rol: "GestorCae");

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Devuelve_el_evento_de_visita_creada_con_su_descripcion()
    {
        Guid conversacionId, visitaId;
        await using (var contexto = CrearContexto())
        {
            var cliente = new Cliente("Cliente Timeline S.L.", "B10380194", esCritico: false);
            var empresa = new Empresa("Empresa Timeline S.L.", "B10380186");
            contexto.Clientes.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro Timeline");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            var conversacion = new Conversacion("Coordinación de visita");
            conversacion.AsignarCliente(cliente.Id);
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();
            conversacionId = conversacion.Id;

            var visita = new Visita(centro.Id, new DateOnly(2026, 8, 17), new DateOnly(2026, 8, 17), null, OrigenVisita.Correo);
            contexto.Visitas.Add(visita);
            await contexto.SaveChangesAsync();
            visitaId = visita.Id;

            contexto.EventosConversacion.Add(new EventoConversacion(conversacionId, TipoEventoConversacion.VisitaCreada, visitaId, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerConversacionPorIdQueryHandler(
            lectura, lectura, lectura, lectura, lectura, lectura, _alcanceDatos, new GanssSanitizadorHtmlService(), _currentUser);

        var detalle = await handler.Handle(new ObtenerConversacionPorIdQuery(conversacionId), CancellationToken.None);

        detalle.Should().NotBeNull();
        detalle!.Eventos.Should().ContainSingle();
        var evento = detalle.Eventos[0];
        evento.Tipo.Should().Be(TipoEventoConversacion.VisitaCreada);
        evento.ReferenciaId.Should().Be(visitaId);
        evento.Descripcion.Should().Contain("Centro Timeline").And.Contain("17/08/2026");
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), tenantActual);
    }

}
