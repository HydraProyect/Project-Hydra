using CaeManager.Application.Plataforma;
using CaeManager.Application.Comunicaciones.Commands.ConfirmarClasificacionRuidoMensaje;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Verifica la reversibilidad de la ronda de reducción de ruido en Comunicaciones (Fase 8): el
/// gestor puede confirmar que un mensaje marcado como ruido sí importa, tanto en su forma de
/// mensaje completo (ClasificacionRuidoMensaje) como por ítem de gestión (repetición de un
/// pendiente ya reclamado, ClasificacionRuidoDetalleGestion) — ambas se confirman con un único
/// Command a nivel de Mensaje.
/// </summary>
public class ConfirmarClasificacionRuidoMensajeCommandTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _clienteId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = new Cliente("Cliente Confirmación Ruido S.L.", "B10380202", esCritico: false);
        contexto.Clientes.Add(cliente);
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Confirma_la_clasificacion_de_ruido_de_un_mensaje_con_resumen_sin_cambios()
    {
        Guid mensajeId;
        await using (var contexto = CrearContexto())
        {
            var conversacion = new Conversacion("Aviso", _clienteId);
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();

            var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "notificaciones@plataforma.com", "Aviso");
            mensajeId = mensaje.Id;
            await contexto.SaveChangesAsync();

            var clasificacion = new ClasificacionRuidoMensaje(mensajeId, true, null, MotivoRuidoMensaje.ResumenSinCambios);
            contexto.ClasificacionesRuidoMensaje.Add(clasificacion);
            await contexto.SaveChangesAsync();
        }

        await using var contextoComando = CrearContexto();
        var handler = new ConfirmarClasificacionRuidoMensajeCommandHandler(
            new ClasificacionRuidoMensajeRepository(contextoComando), new ClasificacionRuidoDetalleGestionRepository(contextoComando),
            contextoComando, new AlcanceDatosService(contextoComando, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"), new TenantActualAmbiental { TenantId = _tenant }, new SesionPrivilegiadaAusente()), contextoComando);

        var resultado = await handler.Handle(new ConfirmarClasificacionRuidoMensajeCommand(mensajeId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();

        await using var contextoVerificacion = CrearContexto();
        var clasificacionActualizada = await contextoVerificacion.ClasificacionesRuidoMensaje.SingleAsync(c => c.MensajeId == mensajeId);
        clasificacionActualizada.ConfirmadaManualmente.Should().BeTrue();
    }

    [Fact]
    public async Task Confirma_los_items_pendientes_marcados_como_repeticion_de_un_mensaje()
    {
        Guid mensajeId;
        Guid detalleId;
        await using (var contexto = CrearContexto())
        {
            var conversacion = new Conversacion("Notificación en bloque", _clienteId);
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();

            var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "notificaciones@plataforma.com", "Aviso");
            mensajeId = mensaje.Id;
            await contexto.SaveChangesAsync();

            var sugerencia = new SugerenciaGestionCorreo(mensajeId, "Un pendiente", 85);
            var detalle = sugerencia.AgregarDetalle(Guid.NewGuid(), Guid.NewGuid(), 90, 90);
            detalleId = detalle.Id;
            contexto.SugerenciasGestionCorreo.Add(sugerencia);
            await contexto.SaveChangesAsync();

            contexto.ClasificacionesRuidoDetalleGestion.Add(new ClasificacionRuidoDetalleGestion(detalleId, Guid.NewGuid()));
            await contexto.SaveChangesAsync();
        }

        await using var contextoComando = CrearContexto();
        var handler = new ConfirmarClasificacionRuidoMensajeCommandHandler(
            new ClasificacionRuidoMensajeRepository(contextoComando), new ClasificacionRuidoDetalleGestionRepository(contextoComando),
            contextoComando, new AlcanceDatosService(contextoComando, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"), new TenantActualAmbiental { TenantId = _tenant }, new SesionPrivilegiadaAusente()), contextoComando);

        var resultado = await handler.Handle(new ConfirmarClasificacionRuidoMensajeCommand(mensajeId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();

        await using var contextoVerificacion = CrearContexto();
        var clasificacionDetalle = await contextoVerificacion.ClasificacionesRuidoDetalleGestion
            .SingleAsync(c => c.DetalleSugerenciaGestionCorreoId == detalleId);
        clasificacionDetalle.ConfirmadaManualmente.Should().BeTrue();
    }

    [Fact]
    public async Task Falla_si_el_mensaje_no_tiene_ninguna_clasificacion_de_ruido()
    {
        Guid mensajeId;
        await using (var contexto = CrearContexto())
        {
            var conversacion = new Conversacion("Correo normal", _clienteId);
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();

            var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "Hola");
            mensajeId = mensaje.Id;
            await contexto.SaveChangesAsync();
        }

        await using var contextoComando = CrearContexto();
        var handler = new ConfirmarClasificacionRuidoMensajeCommandHandler(
            new ClasificacionRuidoMensajeRepository(contextoComando), new ClasificacionRuidoDetalleGestionRepository(contextoComando),
            contextoComando, new AlcanceDatosService(contextoComando, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"), new TenantActualAmbiental { TenantId = _tenant }, new SesionPrivilegiadaAusente()), contextoComando);

        var resultado = await handler.Handle(new ConfirmarClasificacionRuidoMensajeCommand(mensajeId), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ClasificacionRuidoMensaje.NoEncontrada");
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
