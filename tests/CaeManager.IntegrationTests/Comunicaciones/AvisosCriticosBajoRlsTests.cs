using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.IntegrationTests.Comunicaciones;

public class AvisosCriticosBajoRlsTests
{
    [Fact]
    public async Task El_aviso_se_guarda_solo_en_el_Operador_CAE_y_el_reintento_no_lo_duplica()
    {
        var personaId = Guid.NewGuid();
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(false, serviciosAdicionales: servicios =>
        {
            servicios.AddSingleton<IDirectorioUsuariosService>(new DirectorioDePrueba(personaId));
            servicios.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());
            servicios.AddScoped<INotificacionUsuarioRepository, NotificacionUsuarioRepository>();
            servicios.AddScoped<IContextoOperativoConversacionService, ContextoOperativoConversacionService>();
        });
        var propietario = new Tenant("Propietario de la conversación");
        var operador = new Tenant("Operador CAE de prueba");
        var ahora = DateTime.UtcNow;
        var conversacion = new Conversacion("Asunto confidencial que no debe aparecer en el aviso");
        conversacion.CambiarPrioridad(PrioridadConversacion.Critica);
        var eventoId = conversacion.AvisoCriticaPendienteId!.Value;
        var tenantAmbiental = new TenantAmbiental();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(arnes.CadenaPropietario, pg => pg.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantAmbiental)).Options;
        await using (var siembra = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantAmbiental))
        {
            siembra.Tenants.AddRange(propietario, operador);
            await siembra.SaveChangesAsync();
            siembra.AsignacionesOperacion.Add(AsignacionOperacion.Externa(propietario.Id, operador.Id,
                ServicioCae.Outbound, new AmbitoAsignacion(), ahora.AddDays(-1), null, ahora));
            using (AmbitoTenantExplicito.Establecer(propietario.Id))
            {
                siembra.Conversaciones.Add(conversacion);
                await siembra.SaveChangesAsync();
            }
        }

        using var actor = AmbitoActorAuditoria.EstablecerSistema();
        using (AmbitoTenantExplicito.Establecer(propietario.Id))
        using (var scope = arnes.Servicios.CreateScope())
        {
            var contexto = scope.ServiceProvider.GetRequiredService<IContextoOperativoConversacionService>();
            var operacion = await contexto.ResolverAsync(propietario.Id, null, null, CancellationToken.None);
            operacion!.OperadorCaeTenantId.Should().Be(operador.Id);
            await contexto.AvisarCriticaAsync(operacion, eventoId, CancellationToken.None);
            await contexto.AvisarCriticaAsync(operacion, eventoId, CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            (await db.NotificacionesUsuario.CountAsync()).Should().Be(0, "la RLS del Tenant propietario no ve los avisos del Operador CAE");
        }
        var job = new AvisosConversacionesCriticasHostedService(arnes.Servicios.GetRequiredService<IServiceScopeFactory>(),
            new LiderSiempre(), NullLogger<AvisosConversacionesCriticasHostedService>.Instance);
        await job.ProcesarAsync(CancellationToken.None);

        using (AmbitoTenantExplicito.Establecer(operador.Id))
        using (var scope = arnes.Servicios.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var avisos = await db.NotificacionesUsuario.ToListAsync();
            avisos.Should().ContainSingle().Which.UsuarioDestinatarioId.Should().Be(personaId);
            avisos[0].Mensaje.Should().NotContain(conversacion.Asunto);
            (await scope.ServiceProvider.GetRequiredService<IContextoOperativoConversacionService>()
                .ResolverAsync(propietario.Id, null, null, CancellationToken.None)).Should().BeNull();
        }
        using (AmbitoTenantExplicito.Establecer(propietario.Id))
        using (var scope = arnes.Servicios.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>().Conversaciones.SingleAsync())
                .AvisoCriticaPendienteId.Should().BeNull();
    }

    private sealed class TenantAmbiental : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class LiderSiempre : IEleccionLiderService
    {
        public async Task<bool> IntentarEjecutarComoLiderAsync(string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
        {
            await trabajo(cancellationToken);
            return true;
        }
    }

    private sealed class DirectorioDePrueba(Guid personaId) : IDirectorioUsuariosService
    {
        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>(rol == "CoordinadorCae" ? [personaId] : rol == "GestorCae" ? [Guid.NewGuid()] : []);
        public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> EsCuentaActivaConRolAsync(Guid usuarioId, Guid tenantId, string rol, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
