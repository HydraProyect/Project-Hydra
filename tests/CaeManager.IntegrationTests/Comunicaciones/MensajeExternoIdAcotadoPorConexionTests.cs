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
/// Auditoría módulo 6: el índice único de Mensajes.MensajeExternoId sigue
/// siendo por tenant (no por conexión — ver la decisión documentada en
/// MensajeConfiguration), pero las CONSULTAS de idempotencia sí deben
/// acotarse por conexión: sin eso, un Message-Id/wamid que ya pertenece al
/// Mensaje de UN buzón haría que la ingesta de OTRO buzón del mismo tenant
/// lo tratara como duplicado (pérdida silenciosa de un mensaje real) o
/// aplicara un status de entrega al Mensaje equivocado.
/// </summary>
public class MensajeExternoIdAcotadoPorConexionTests : IAsyncLifetime
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

    [Fact]
    public async Task ExisteMensajeExterno_no_ve_el_mensaje_de_otra_conexion_con_el_mismo_id_externo()
    {
        const string idExterno = "wamid.compartido-entre-lineas";
        var conexionA = Guid.NewGuid();
        var conexionB = Guid.NewGuid();

        await using (var contexto = CrearContexto())
        {
            var conversacionA = Conversacion.CrearWhatsApp("+34600000001", conexionA, null, null);
            conversacionA.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600000001", "Hola desde A", mensajeExternoId: idExterno);
            contexto.Conversaciones.Add(conversacionA);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = CrearContexto())
        {
            var repositorio = new ConversacionRepository(contexto);

            var existeParaSuPropiaConexion = await repositorio.ExisteMensajeExternoAsync(conexionA, idExterno);
            var existeParaOtraConexion = await repositorio.ExisteMensajeExternoAsync(conexionB, idExterno);

            existeParaSuPropiaConexion.Should().BeTrue();
            existeParaOtraConexion.Should().BeFalse(
                "el mensaje pertenece a la línea A — la ingesta de la línea B no debe tratarlo como ya recibido");
        }
    }

    [Fact]
    public async Task ObtenerMensajePorExternoId_no_devuelve_el_mensaje_de_otra_conexion_con_el_mismo_id_externo()
    {
        const string idExterno = "wamid.status-cruzado";
        var conexionA = Guid.NewGuid();
        var conexionB = Guid.NewGuid();
        Guid mensajeAId;

        await using (var contexto = CrearContexto())
        {
            var conversacionA = Conversacion.CrearWhatsApp("+34600000002", conexionA, null, null);
            var mensajeA = conversacionA.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.WhatsApp, "+34686000000", "Respuesta de A", mensajeExternoId: idExterno);
            contexto.Conversaciones.Add(conversacionA);
            await contexto.SaveChangesAsync();
            mensajeAId = mensajeA.Id;
        }

        await using (var contexto = CrearContexto())
        {
            var repositorio = new ConversacionRepository(contexto);

            var encontradoParaSuPropiaConexion = await repositorio.ObtenerMensajePorExternoIdAsync(conexionA, idExterno);
            var encontradoParaOtraConexion = await repositorio.ObtenerMensajePorExternoIdAsync(conexionB, idExterno);

            encontradoParaSuPropiaConexion!.Id.Should().Be(mensajeAId);
            encontradoParaOtraConexion.Should().BeNull(
                "un status de la línea B no debe poder actualizar un mensaje que pertenece a la línea A");
        }
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
