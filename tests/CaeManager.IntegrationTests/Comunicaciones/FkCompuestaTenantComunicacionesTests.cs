using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Auditoría Módulo 8: prueba la FK compuesta (Xxx, TenantId) → padre
/// (Id, TenantId) que la migración FkCompuestaTenantComunicaciones añade en
/// SQL crudo para Mensaje/ParticipanteConversacion (hijos de Conversacion) y
/// AdjuntoMensaje (hijo de Mensaje) — invisible al modelo Fluent a propósito
/// (ver ConversacionConfiguration/MensajeConfiguration), así que solo un
/// UPDATE crudo que la viole puede demostrar que existe de verdad. Mismo
/// patrón que CheckXorPropietarioDocumentoTests: UPDATE directo sobre la
/// columna que rompe la regla, no un INSERT completo.
/// </summary>
public class FkCompuestaTenantComunicacionesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    private Guid _mensajeId;
    private Guid _participanteId;
    private Guid _adjuntoId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenantA);
        await contexto.Database.MigrateAsync();

        var conversacion = new Conversacion("Hilo de prueba");
        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "<p>Cuerpo</p>");
        var adjunto = mensaje.AgregarAdjunto("factura.pdf", "application/pdf", 1024, "blob://factura");
        var participante = conversacion.AgregarParticipante("cliente@ejemplo.com", RolParticipante.Para, TipoParticipanteOrigen.Desconocido);

        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        _mensajeId = mensaje.Id;
        _adjuntoId = adjunto.Id;
        _participanteId = participante.Id;

        // La conversación de otro tenant existe para que el UPDATE no falle
        // por un simple NOT NULL/tenant inexistente, sino específicamente
        // porque (ConversacionId, TenantB) no casa con ninguna fila de
        // Conversaciones — la FK compuesta, no una casualidad de datos vacíos.
        await using var contextoB = CrearContexto(_tenantB);
        contextoB.Conversaciones.Add(new Conversacion("Hilo del otro tenant"));
        await contextoB.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task No_se_puede_reasignar_un_mensaje_al_tenant_de_otra_conversacion()
    {
        await using var contexto = CrearContexto(_tenantA);

        var excepcion = await Record.ExceptionAsync(() => contexto.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Mensajes\" SET \"TenantId\" = {_tenantB} WHERE \"Id\" = {_mensajeId}"));

        excepcion.Should().NotBeNull("la FK compuesta (ConversacionId, TenantId) debe rechazar un TenantId que no case con el de la Conversacion");
    }

    [Fact]
    public async Task No_se_puede_reasignar_un_participante_al_tenant_de_otra_conversacion()
    {
        await using var contexto = CrearContexto(_tenantA);

        var excepcion = await Record.ExceptionAsync(() => contexto.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"ParticipantesConversacion\" SET \"TenantId\" = {_tenantB} WHERE \"Id\" = {_participanteId}"));

        excepcion.Should().NotBeNull("la FK compuesta (ConversacionId, TenantId) debe rechazar un TenantId que no case con el de la Conversacion");
    }

    [Fact]
    public async Task No_se_puede_reasignar_un_adjunto_al_tenant_de_otro_mensaje()
    {
        await using var contexto = CrearContexto(_tenantA);

        var excepcion = await Record.ExceptionAsync(() => contexto.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AdjuntosMensaje\" SET \"TenantId\" = {_tenantB} WHERE \"Id\" = {_adjuntoId}"));

        excepcion.Should().NotBeNull("la FK compuesta (MensajeId, TenantId) debe rechazar un TenantId que no case con el del Mensaje");
    }

    private CaeManagerDbContext CrearContexto(Guid tenant)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
