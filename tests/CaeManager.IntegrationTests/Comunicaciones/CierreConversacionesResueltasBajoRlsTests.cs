using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Comunicaciones;
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
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// <b>El cierre automático de conversaciones resueltas (decisión 2026-10-17, hueco 2 de Comunicaciones) y el historial de
/// cambios de estado, tal como corren en producción:</b> conectado como <c>cae_app_runtime</c>, con los interceptores reales
/// de sesión RLS, sellado y auditoría, y sin usuario. Mismo arnés que <c>CierreEventosTerminadosBajoRlsTests</c>. Prueba lo que
/// los tests de dominio no pueden: que la tabla nueva <c>CambiosEstadoConversacion</c> existe con su RLS y su FK compuesta,
/// que el proceso ve las filas de cada Tenant propietario (también el suspendido) y que el cierre queda como acción del
/// Sistema. La siembra va como propietario de la base, porque no es lo que se mide.
/// </summary>
public class CierreConversacionesResueltasBajoRlsTests
{
    [Fact]
    public async Task Bajo_el_rol_de_runtime_cierra_las_resueltas_hace_mas_de_cuatro_dias_de_cada_Tenant_y_deja_lo_demas_como_estaba()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var ahora = DateTime.UtcNow;

        var tenantA = new Tenant("Tenant propietario con bandeja activa");
        var tenantSuspendido = new Tenant("Tenant propietario suspendido con conversaciones");
        tenantSuspendido.Suspender();

        Conversacion resueltaVieja, resueltaReciente, abiertaVieja, cerradaAMano, resueltaViejaDelSuspendido;
        var tenantPorAmbito = new TenantActualPorAmbito();
        await using (var propietarioBd = CrearContextoPropietario(arnes.CadenaPropietario, tenantPorAmbito))
        {
            propietarioBd.Tenants.AddRange(tenantA, tenantSuspendido);
            await propietarioBd.SaveChangesAsync();

            using (AmbitoTenantExplicito.Establecer(tenantA.Id))
            {
                resueltaVieja = Sembrar("Resuelta hace cinco días", EstadoConversacion.Resuelta, ahora.AddDays(-5));
                resueltaReciente = Sembrar("Resuelta ayer", EstadoConversacion.Resuelta, ahora.AddDays(-1));
                abiertaVieja = Sembrar("Abierta desde hace semanas", EstadoConversacion.Abierta, ahora.AddDays(-30));
                cerradaAMano = Sembrar("Cerrada a mano", EstadoConversacion.Cerrada, ahora.AddDays(-9));
                propietarioBd.Conversaciones.AddRange(resueltaVieja, resueltaReciente, abiertaVieja, cerradaAMano);
                await propietarioBd.SaveChangesAsync();
            }

            using (AmbitoTenantExplicito.Establecer(tenantSuspendido.Id))
            {
                resueltaViejaDelSuspendido = Sembrar("Resuelta hace diez días", EstadoConversacion.Resuelta, ahora.AddDays(-10));
                propietarioBd.Conversaciones.Add(resueltaViejaDelSuspendido);
                await propietarioBd.SaveChangesAsync();
            }
        }

        var job = new CierreConversacionesResueltasHostedService(
            arnes.Servicios.GetRequiredService<IServiceScopeFactory>(),
            new LiderSiempre(),
            NullLogger<CierreConversacionesResueltasHostedService>.Instance);

        await job.ProcesarAsync(ahora, CancellationToken.None);

        await using var verificacion = CrearContextoPropietario(arnes.CadenaPropietario, new TenantActualPorAmbito());
        async Task<Conversacion> Leer(Guid id) =>
            await verificacion.Conversaciones.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == id);

        (await Leer(resueltaVieja.Id)).Estado.Should().Be(EstadoConversacion.Cerrada,
            "bajo cae_app_runtime el proceso tiene que ver la conversación resuelta hace más de 4 días y cerrarla");
        (await Leer(resueltaReciente.Id)).Estado.Should().Be(EstadoConversacion.Resuelta, "todavía no han pasado los 4 días");
        (await Leer(abiertaVieja.Id)).Estado.Should().Be(EstadoConversacion.Abierta, "solo se cierran las Resueltas");
        (await Leer(cerradaAMano.Id)).Estado.Should().Be(EstadoConversacion.Cerrada);
        (await Leer(resueltaViejaDelSuspendido.Id)).Estado.Should().Be(EstadoConversacion.Cerrada,
            "el plazo de una conversación no deja de correr porque su Tenant propietario esté suspendido");

        var cambios = await verificacion.CambiosEstadoConversacion.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.ConversacionId == resueltaVieja.Id)
            .OrderBy(c => c.FechaUtc)
            .ToListAsync();
        cambios.Select(c => (c.EstadoAnterior, c.EstadoNuevo)).Should().Equal(
            (EstadoConversacion.Abierta, EstadoConversacion.Resuelta),
            (EstadoConversacion.Resuelta, EstadoConversacion.Cerrada));
        cambios[^1].FechaUtc.Should().BeCloseTo(ahora, TimeSpan.FromSeconds(1), "cada cambio guarda su instante");
        cambios.Should().OnlyContain(c => c.TenantId == tenantA.Id, "el historial hereda el Tenant propietario de la conversación");

        var auditoria = await verificacion.RegistrosAuditoria.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.EntidadId == resueltaVieja.Id && r.TipoActor == TipoActorAuditoria.Sistema)
            .ToListAsync();
        auditoria.Should().NotBeEmpty("el cierre automático queda auditado como acción del Sistema, sin persona detrás");
    }

    /// <summary>
    /// La reapertura por mensaje entrante y el cambio de estado a mano se guardan como lo hacen los comandos: cargando la
    /// conversación por su repositorio (sin el historial) bajo <c>cae_app_runtime</c> y el Tenant propietario. Si el cambio
    /// de estado no pudiese insertar su fila en <c>CambiosEstadoConversacion</c> (RLS, FK compuesta, concesión de permisos), esto
    /// revienta aquí y no en producción.
    /// </summary>
    [Fact]
    public async Task Bajo_el_rol_de_runtime_la_reapertura_por_mensaje_entrante_y_el_cambio_a_mano_guardan_su_cambio()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var ahora = DateTime.UtcNow;
        var tenant = new Tenant("Tenant propietario que recibe respuestas");
        Conversacion resuelta, enEspera;

        await using (var propietarioBd = CrearContextoPropietario(arnes.CadenaPropietario, new TenantActualPorAmbito()))
        {
            propietarioBd.Tenants.Add(tenant);
            await propietarioBd.SaveChangesAsync();
            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                resuelta = Sembrar("Resuelta", EstadoConversacion.Resuelta, ahora.AddDays(-1));
                enEspera = Sembrar("Esperando", EstadoConversacion.Abierta, ahora.AddDays(-1));
                propietarioBd.Conversaciones.AddRange(resuelta, enEspera);
                await propietarioBd.SaveChangesAsync();
            }
        }

        using (AmbitoTenantExplicito.Establecer(tenant.Id))
        {
            using var ambito = arnes.Servicios.CreateScope();
            var runtime = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var repositorio = new ConversacionRepository(runtime);

            var cargada = (await repositorio.ObtenerPorIdAsync(resuelta.Id))!;
            cargada.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "carmen@cliente.es", "<p>Una duda más.</p>", ahora);
            var cambio = (await repositorio.ObtenerPorIdAsync(enEspera.Id))!;
            cambio.CambiarEstado(EstadoConversacion.EsperandoRespuestaExterna, ahora).EsExitoso.Should().BeTrue();
            await runtime.SaveChangesAsync();
        }

        await using var verificacion = CrearContextoPropietario(arnes.CadenaPropietario, new TenantActualPorAmbito());
        (await verificacion.Conversaciones.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == resuelta.Id))
            .Estado.Should().Be(EstadoConversacion.Abierta, "el mensaje entrante reabrió la conversación resuelta");
        var cambios = await verificacion.CambiosEstadoConversacion.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.ConversacionId == resuelta.Id || c.ConversacionId == enEspera.Id)
            .ToListAsync();
        cambios.Select(c => (c.ConversacionId, c.EstadoAnterior, c.EstadoNuevo)).Should().BeEquivalentTo(
        [
            (resuelta.Id, EstadoConversacion.Abierta, EstadoConversacion.Resuelta),
            (resuelta.Id, EstadoConversacion.Resuelta, EstadoConversacion.Abierta),
            (enEspera.Id, EstadoConversacion.Abierta, EstadoConversacion.EsperandoRespuestaExterna)
        ]);

        // El historial no es auditoría de personas: ninguna fila del historial de estados aparece en RegistrosAuditoria
        // (la auditoría genérica guardaría quién y cuándo, y la decisión es que el historial nunca guarda quién).
        var idsDeCambios = cambios.Select(c => c.Id).ToList();
        (await verificacion.RegistrosAuditoria.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(r => idsDeCambios.Contains(r.EntidadId))).Should().BeFalse();
    }

    /// <summary>
    /// La consulta que alimenta la sugerencia de vinculación, contra PostgreSQL real: <c>EstadosVivos.Contains</c> tiene que
    /// traducirse a SQL y devolver las conversaciones en espera (los dos estados nuevos) y no las Resueltas ni las Cerradas.
    /// </summary>
    [Fact]
    public async Task Bajo_el_rol_de_runtime_las_abiertas_del_cliente_incluyen_los_estados_de_espera_y_excluyen_resueltas_y_cerradas()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var ahora = DateTime.UtcNow;
        var tenant = new Tenant("Tenant propietario con hilos del mismo Cliente empresarial");
        var clienteId = Guid.NewGuid();
        var porEstado = new Dictionary<EstadoConversacion, Conversacion>();

        await using (var propietarioBd = CrearContextoPropietario(arnes.CadenaPropietario, new TenantActualPorAmbito()))
        {
            propietarioBd.Tenants.Add(tenant);
            await propietarioBd.SaveChangesAsync();
            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                foreach (var estado in Enum.GetValues<EstadoConversacion>())
                {
                    var conversacion = Sembrar("Hilo " + estado, estado, ahora.AddHours(-2));
                    conversacion.AsignarCliente(clienteId);
                    porEstado[estado] = conversacion;
                    propietarioBd.Conversaciones.Add(conversacion);
                }
                await propietarioBd.SaveChangesAsync();
            }
        }

        IReadOnlyList<Conversacion> vivas;
        using (AmbitoTenantExplicito.Establecer(tenant.Id))
        {
            using var ambito = arnes.Servicios.CreateScope();
            var repositorio = new ConversacionRepository(ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>());
            vivas = await repositorio.ObtenerAbiertasPorClienteAsync(clienteId, Guid.NewGuid());
        }

        vivas.Select(c => c.Estado).Should().BeEquivalentTo(
        [
            EstadoConversacion.Abierta,
            EstadoConversacion.Pendiente,
            EstadoConversacion.EsperandoRespuestaExterna,
            EstadoConversacion.EnEsperaDeTercero
        ]);
    }

    /// <summary>Una conversación de triage (sin Cliente) llevada a <paramref name="estado"/> en el instante <paramref name="desdeUtc"/>.</summary>
    private static Conversacion Sembrar(string asunto, EstadoConversacion estado, DateTime desdeUtc)
    {
        var conversacion = new Conversacion(asunto);
        if (estado != EstadoConversacion.Abierta)
            conversacion.CambiarEstado(estado, desdeUtc).EsExitoso.Should().BeTrue();
        return conversacion;
    }

    private static CaeManagerDbContext CrearContextoPropietario(string cadenaPropietario, TenantActualPorAmbito tenantPorAmbito)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;

        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantPorAmbito);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class LiderSiempre : IEleccionLiderService
    {
        public async Task<bool> IntentarEjecutarComoLiderAsync(
            string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
        {
            await trabajo(cancellationToken);
            return true;
        }
    }
}
