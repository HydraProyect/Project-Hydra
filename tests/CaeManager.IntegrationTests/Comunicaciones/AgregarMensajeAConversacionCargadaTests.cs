using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Regresión del flujo real de ingesta (detectada verificando WhatsApp en
/// local, 2026-08-04): el SEGUNDO mensaje de un hilo se añade a una
/// conversación CARGADA de la base de datos — el mensaje nuevo lo descubre
/// EF por fixup de la navegación (colección con campo privado), no por un
/// Add explícito, y su estado/TenantId deben quedar bien ante
/// TenantSelladoInterceptor. Cubre ambos canales porque el flujo de correo
/// (IngestaWebhookService) hace exactamente lo mismo.
/// </summary>
public class AgregarMensajeAConversacionCargadaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task El_segundo_mensaje_whatsapp_de_un_hilo_cargado_se_persiste()
    {
        var conexionId = Guid.NewGuid();
        Guid conversacionId;

        await using (var contexto = CrearContexto())
        {
            var conexion = new CaeManager.Domain.Integraciones.ConexionIntegracion(
                "+34686543364", "Línea de prueba", proveedor: CaeManager.Domain.Integraciones.ProveedorIntegracion.WhatsApp);
            contexto.ConexionesIntegracion.Add(conexion);
            conexionId = conexion.Id;

            var conversacion = ConversacionCorreo.CrearWhatsApp("+34600111222", conexion.Id, null, Guid.NewGuid());
            conversacion.AgregarMensaje(DireccionMensaje.Entrante, "+34600111222", "Primero", mensajeExternoId: "wamid.repro.1");
            contexto.ConversacionesCorreo.Add(conversacion);
            await contexto.SaveChangesAsync();
            conversacionId = conversacion.Id;
        }

        // Scope nuevo, como cada iteración del hosted service: la conversación
        // llega TRACKED desde la consulta y el mensaje nuevo entra por fixup.
        await using (var contexto = CrearContexto())
        {
            var repositorio = new ConversacionCorreoRepository(contexto);
            var conversacion = await repositorio.ObtenerAbiertaPorTelefonoAsync(conexionId, "+34600111222");
            conversacion.Should().NotBeNull();

            conversacion!.AgregarMensaje(DireccionMensaje.Entrante, "+34600111222", "Segundo", mensajeExternoId: "wamid.repro.2");
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var mensajes = await contexto.MensajesCorreo.CountAsync(m => m.ConversacionCorreoId == conversacionId);
            mensajes.Should().Be(2);
        }
    }

    [Fact]
    public async Task La_respuesta_a_un_hilo_de_correo_cargado_se_persiste()
    {
        Guid conversacionId;

        await using (var contexto = CrearContexto())
        {
            var conversacion = new ConversacionCorreo("Hilo de correo");
            conversacion.AsociarConexion(Guid.NewGuid(), "hilo-repro-1");
            conversacion.AgregarMensaje(DireccionMensaje.Entrante, "cliente@ejemplo.com", "<p>Primero</p>", mensajeExternoId: "graph.repro.1");
            contexto.ConversacionesCorreo.Add(conversacion);
            await contexto.SaveChangesAsync();
            conversacionId = conversacion.Id;
        }

        await using (var contexto = CrearContexto())
        {
            var repositorio = new ConversacionCorreoRepository(contexto);
            var conversacion = await repositorio.ObtenerPorHiloExternoAsync("hilo-repro-1");
            conversacion.Should().NotBeNull();

            conversacion!.AgregarMensaje(DireccionMensaje.Entrante, "cliente@ejemplo.com", "<p>Segundo</p>", mensajeExternoId: "graph.repro.2");
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var mensajes = await contexto.MensajesCorreo.CountAsync(m => m.ConversacionCorreoId == conversacionId);
            mensajes.Should().Be(2);
        }
    }
}
