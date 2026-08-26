using CaeManager.Application.Comunicaciones.Commands.VincularConversacion;
using CaeManager.Application.Comunicaciones.Matching;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Conversation Matching Engine end-to-end (docs/COMUNICACIONES.md § 13.2)
/// contra Postgres real: la propuesta que calcula ObtenerConversacionPorIdQuery
/// y la fusión real que ejecuta VincularConversacionCommand al confirmarla.
/// </summary>
public class VincularConversacionCommandTests : IAsyncLifetime
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
    public async Task Propone_vincular_y_al_confirmar_fusiona_los_mensajes_en_un_hilo_mixto()
    {
        Guid clienteId, conversacionWhatsAppId, conversacionCorreoId, mensajeWhatsAppId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Matching S.L.", "B10380194", false, null, null);
            contexto.Empresas.Add(cliente);
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id;

            var conversacionCorreo = new Conversacion("Consulta sobre la próxima visita", clienteId);
            conversacionCorreo.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "contacto@cliente.test", "¿Cuándo viene el técnico?");
            contexto.Conversaciones.Add(conversacionCorreo);
            await contexto.SaveChangesAsync();
            conversacionCorreoId = conversacionCorreo.Id;

            var conversacionWhatsApp = Conversacion.CrearWhatsApp("+34600111222", Guid.NewGuid(), clienteId, null);
            var mensajeWhatsApp = conversacionWhatsApp.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.WhatsApp, "+34600111222", "Hola, ¿sabéis algo de la visita?");
            contexto.Conversaciones.Add(conversacionWhatsApp);
            await contexto.SaveChangesAsync();
            conversacionWhatsAppId = conversacionWhatsApp.Id;
            mensajeWhatsAppId = mensajeWhatsApp.Id;
        }

        // 1. La propuesta aparece al leer el hilo WhatsApp (misma actividad reciente que el hilo de correo).
        await using (var lectura = CrearContexto())
        {
            var handlerQuery = new ObtenerConversacionPorIdQueryHandler(
                lectura, lectura, lectura, lectura, lectura, lectura, lectura, lectura, _alcanceDatos, new GanssSanitizadorHtmlService(), _currentUser,
                new MotorCoincidenciaConversacionesService(new ConversacionRepository(lectura)));

            var detalle = await handlerQuery.Handle(new ObtenerConversacionPorIdQuery(conversacionWhatsAppId), CancellationToken.None);

            detalle.Should().NotBeNull();
            detalle!.SugerenciaVinculacion.Should().NotBeNull();
            detalle.SugerenciaVinculacion!.ConversacionDestinoId.Should().Be(conversacionCorreoId);
            detalle.SugerenciaVinculacion.Desglose.Total.Should().Be(50);
        }

        // 2. Confirmar la propuesta funde de verdad los dos hilos.
        await using (var escritura = CrearContexto())
        {
            var handlerCommand = new VincularConversacionCommandHandler(
                new ConversacionRepository(escritura), new EventoConversacionRepository(escritura), _alcanceDatos, escritura);

            var resultado = await handlerCommand.Handle(
                new VincularConversacionCommand(conversacionWhatsAppId, conversacionCorreoId), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        // 3. Verificación final directa contra la base de datos.
        await using (var verificacion = CrearContexto())
        {
            var mensajeMovido = await verificacion.Mensajes.FirstAsync(m => m.Id == mensajeWhatsAppId);
            mensajeMovido.ConversacionId.Should().Be(conversacionCorreoId);
            mensajeMovido.Canal.Should().Be(CanalConversacion.WhatsApp);

            var origen = await verificacion.Conversaciones.FirstAsync(c => c.Id == conversacionWhatsAppId);
            origen.Estado.Should().Be(EstadoConversacion.Cerrada);
            origen.Mensajes.Should().BeEmpty();

            var evento = await verificacion.EventosConversacion.SingleAsync(e => e.ConversacionId == conversacionCorreoId);
            evento.Tipo.Should().Be(TipoEventoConversacion.ConversacionVinculada);
            evento.ReferenciaId.Should().Be(conversacionWhatsAppId);
        }
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
