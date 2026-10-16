using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Proyectos;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Proyectos;

/// <summary>
/// <b>El cierre automático de eventos terminados (decisión 2026-10-16), tal como corre en producción:</b> conectado como
/// <c>cae_app_runtime</c>, con los interceptores reales de sesión RLS, sellado y auditoría, y sin usuario. Mismo arnés
/// que <c>ExpiracionAsignacionesBajoRlsTests</c>. Sin el <c>AmbitoTenantExplicito</c> por Tenant propietario, la RLS de
/// <c>Proyectos</c> dejaría ver cero filas y ningún evento se cerraría. La siembra va como propietario de la base,
/// porque no es lo que se mide.
/// </summary>
public class CierreEventosTerminadosBajoRlsTests
{
    [Fact]
    public async Task Bajo_el_rol_de_runtime_cierra_los_eventos_pasados_de_cada_Tenant_propietario_y_deja_lo_demas_como_estaba()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var hoy = DiaDeNegocio.Hoy();

        var tenantA = new Tenant("Tenant propietario que organiza partidos");
        var tenantSuspendido = new Tenant("Tenant propietario suspendido con eventos");
        tenantSuspendido.Suspender();

        Proyecto eventoPasado, eventoQueAcabaHoy, obraPasada, eventoCerradoAMano, eventoPasadoDelSuspendido;
        ProyectoTecnico participantePasado, participanteYaDeBaja, participanteDeHoy, participanteDeObra, participanteTardio;

        var tenantPorAmbito = new TenantActualPorAmbito();
        await using (var propietarioBd = CrearContextoPropietario(arnes.CadenaPropietario, tenantPorAmbito))
        {
            propietarioBd.Tenants.AddRange(tenantA, tenantSuspendido);
            await propietarioBd.SaveChangesAsync();

            using (AmbitoTenantExplicito.Establecer(tenantA.Id))
            {
                var (club, estadio) = await SembrarClienteYCentroAsync(propietarioBd, "Club Deportivo Springfield", "G10380186");
                eventoPasado = Proyecto.Crear(club, estadio, "Jornada 1", hoy.AddDays(-4), hoy.AddDays(-2), null, TipoProyecto.Evento);
                eventoQueAcabaHoy = Proyecto.Crear(club, estadio, "Jornada 2", hoy.AddDays(-1), hoy, null, TipoProyecto.Evento);
                obraPasada = Proyecto.Crear(club, estadio, "Reforma de gradas", hoy.AddDays(-60), hoy.AddDays(-5), null);
                eventoCerradoAMano = Proyecto.Crear(club, estadio, "Jornada suspendida", hoy.AddDays(-10), hoy.AddDays(-8), null, TipoProyecto.Evento);
                eventoCerradoAMano.Cerrar(hoy.AddDays(-10));
                propietarioBd.Proyectos.AddRange(eventoPasado, eventoQueAcabaHoy, obraPasada, eventoCerradoAMano);
                await propietarioBd.SaveChangesAsync();

                var ana = Trabajador.DeEmpresa(club, "Ana", "Vidal Ruiz", "45678912S");
                var iker = Trabajador.DeEmpresa(club, "Iker", "Sanz Mora", "12345678Z");
                var lola = Trabajador.DeEmpresa(club, "Lola", "Mena Soto", "87654321X");
                propietarioBd.Trabajadores.AddRange(ana, iker, lola);
                await propietarioBd.SaveChangesAsync();
                participantePasado = new ProyectoTecnico(eventoPasado.Id, ana.Id, hoy.AddDays(-4));
                participanteYaDeBaja = new ProyectoTecnico(eventoPasado.Id, iker.Id, hoy.AddDays(-4));
                participanteYaDeBaja.DarDeBaja(hoy.AddDays(-3));
                // Entró después del fin del evento (un alta tardía): su baja no puede ser anterior a su alta.
                participanteTardio = new ProyectoTecnico(eventoPasado.Id, lola.Id, hoy.AddDays(-1));
                participanteDeHoy = new ProyectoTecnico(eventoQueAcabaHoy.Id, ana.Id, hoy.AddDays(-1));
                participanteDeObra = new ProyectoTecnico(obraPasada.Id, iker.Id, hoy.AddDays(-60));
                propietarioBd.ProyectosTecnicos.AddRange(participantePasado, participanteYaDeBaja, participanteTardio, participanteDeHoy, participanteDeObra);
                await propietarioBd.SaveChangesAsync();
            }

            using (AmbitoTenantExplicito.Establecer(tenantSuspendido.Id))
            {
                var (cliente, centro) = await SembrarClienteYCentroAsync(propietarioBd, "Patronato Shelbyville", "G87654323");
                eventoPasadoDelSuspendido = Proyecto.Crear(cliente, centro, "Torneo", hoy.AddDays(-3), hoy.AddDays(-1), null, TipoProyecto.Evento);
                propietarioBd.Proyectos.Add(eventoPasadoDelSuspendido);
                await propietarioBd.SaveChangesAsync();
            }
        }

        var job = new CierreEventosTerminadosHostedService(
            arnes.Servicios.GetRequiredService<IServiceScopeFactory>(),
            new LiderSiempre(),
            NullLogger<CierreEventosTerminadosHostedService>.Instance);

        await job.ProcesarAsync(CancellationToken.None);

        await using var verificacion = CrearContextoPropietario(arnes.CadenaPropietario, new TenantActualPorAmbito());
        async Task<Proyecto> Leer(Guid id) =>
            await verificacion.Proyectos.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == id);

        (await Leer(eventoPasado.Id)).FechaCierreReal.Should().Be(hoy.AddDays(-2),
            "bajo cae_app_runtime el proceso tiene que ver el evento pasado y cerrarlo en su fecha de fin");
        (await Leer(eventoQueAcabaHoy.Id)).EstaAbierto.Should().BeTrue("el último día del evento cuenta como vigente");
        (await Leer(obraPasada.Id)).EstaAbierto.Should().BeTrue("las obras no se cierran solas");
        (await Leer(eventoCerradoAMano.Id)).FechaCierreReal.Should().Be(hoy.AddDays(-10), "el cierre a mano no se reescribe");
        (await Leer(eventoPasadoDelSuspendido.Id)).FechaCierreReal.Should().Be(hoy.AddDays(-1),
            "la fecha de un evento no deja de pasar porque su Tenant propietario esté suspendido");

        async Task<ProyectoTecnico> LeerParticipante(Guid id) =>
            await verificacion.ProyectosTecnicos.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == id);
        (await LeerParticipante(participantePasado.Id)).FechaBaja.Should().Be(hoy.AddDays(-2),
            "el cierre automático da de baja a los participantes vigentes en la fecha de fin del evento");
        (await LeerParticipante(participanteYaDeBaja.Id)).FechaBaja.Should().Be(hoy.AddDays(-3), "una baja anterior no se reescribe");
        (await LeerParticipante(participanteTardio.Id)).FechaBaja.Should().Be(hoy.AddDays(-1),
            "quien entró después del fin se da de baja en su propio alta, sin abortar el cierre del Tenant");
        (await LeerParticipante(participanteDeHoy.Id)).FechaBaja.Should().BeNull("su evento sigue vigente hoy");
        (await LeerParticipante(participanteDeObra.Id)).FechaBaja.Should().BeNull("las obras no se tocan");

        var auditoria = await verificacion.RegistrosAuditoria.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.EntidadId == eventoPasado.Id && r.TipoActor == TipoActorAuditoria.Sistema)
            .ToListAsync();
        auditoria.Should().ContainSingle("el cierre automático queda auditado como acción del Sistema, sin persona detrás");
    }

    /// <summary>
    /// El ciclo completo contra la base: el proceso cierra el evento; la persona lo reabre con una fecha de fin futura
    /// (la escritura de <c>ReabrirProyectoCommand</c>: <see cref="Proyecto.Reabrir"/> con la nueva fecha, bajo
    /// <c>cae_app_runtime</c> y el Tenant propietario) y queda auditada con actor Persona; el pase siguiente ya no lo
    /// cierra, porque vuelve a contar; y sus participantes no se reactivan solos.
    /// </summary>
    [Fact]
    public async Task Cerrado_por_el_proceso_y_reabierto_con_fecha_futura_queda_auditado_como_Persona_y_el_pase_siguiente_no_lo_cierra()
    {
        var usuarioId = Guid.NewGuid();
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false, actorAuditoriaPersonalizado: new ActorFijo(ActorAuditoria.Normal(usuarioId)));
        var hoy = DiaDeNegocio.Hoy();

        var tenant = new Tenant("Tenant propietario que reprograma un partido");
        Proyecto evento;
        ProyectoTecnico participante;
        await using (var propietarioBd = CrearContextoPropietario(arnes.CadenaPropietario, new TenantActualPorAmbito()))
        {
            propietarioBd.Tenants.Add(tenant);
            await propietarioBd.SaveChangesAsync();
            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                var (club, estadio) = await SembrarClienteYCentroAsync(propietarioBd, "Club Deportivo Springfield", "G10380186");
                evento = Proyecto.Crear(club, estadio, "Jornada aplazada", hoy.AddDays(-3), hoy.AddDays(-1), null, TipoProyecto.Evento);
                propietarioBd.Proyectos.Add(evento);
                await propietarioBd.SaveChangesAsync();
                var ana = Trabajador.DeEmpresa(club, "Ana", "Vidal Ruiz", "45678912S");
                propietarioBd.Trabajadores.Add(ana);
                await propietarioBd.SaveChangesAsync();
                participante = new ProyectoTecnico(evento.Id, ana.Id, hoy.AddDays(-3));
                propietarioBd.ProyectosTecnicos.Add(participante);
                await propietarioBd.SaveChangesAsync();
            }
        }

        var job = new CierreEventosTerminadosHostedService(
            arnes.Servicios.GetRequiredService<IServiceScopeFactory>(),
            new LiderSiempre(),
            NullLogger<CierreEventosTerminadosHostedService>.Instance);
        await job.ProcesarAsync(CancellationToken.None);

        using (AmbitoTenantExplicito.Establecer(tenant.Id))
        using (var ambito = arnes.Servicios.CreateScope())
        {
            var runtime = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var cerrado = await runtime.Proyectos.SingleAsync(p => p.Id == evento.Id);
            cerrado.EstaAbierto.Should().BeFalse("precondición: el proceso lo cerró");
            cerrado.Reabrir(hoy, nuevaFechaFinPrevista: hoy.AddDays(7));
            await runtime.SaveChangesAsync();
        }

        await job.ProcesarAsync(CancellationToken.None);

        await using var verificacion = CrearContextoPropietario(arnes.CadenaPropietario, new TenantActualPorAmbito());
        var reabierto = await verificacion.Proyectos.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == evento.Id);
        reabierto.EstaAbierto.Should().BeTrue("con la nueva fecha de fin el evento vuelve a contar y el pase siguiente no lo cierra");
        reabierto.FechaFinPrevista.Should().Be(hoy.AddDays(7));
        (await verificacion.ProyectosTecnicos.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == participante.Id))
            .FechaBaja.Should().Be(hoy.AddDays(-1), "al reabrir, los participantes no se reactivan solos: se vuelven a asignar");

        var registros = await verificacion.RegistrosAuditoria.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.EntidadId == evento.Id && r.EntidadTipo == nameof(Proyecto))
            .OrderBy(r => r.FechaUtc)
            .Select(r => new { r.TipoActor, r.UsuarioId })
            .ToListAsync();
        registros.Should().Contain(r => r.TipoActor == TipoActorAuditoria.Sistema, "el cierre lo hizo el proceso");
        registros.Should().Contain(r => r.TipoActor == TipoActorAuditoria.Persona && r.UsuarioId == usuarioId,
            "la reapertura queda auditada a nombre de la persona que la hizo");
    }

    private static async Task<(Guid Cliente, Guid Centro)> SembrarClienteYCentroAsync(
        CaeManagerDbContext propietarioBd, string nombre, string cif)
    {
        var empresa = new Empresa(nombre, cif);
        propietarioBd.Empresas.Add(empresa);
        await propietarioBd.SaveChangesAsync();
        var centro = new Centro(empresa.Id, empresa.Id, "Estadio municipal");
        propietarioBd.Add(centro);
        await propietarioBd.SaveChangesAsync();
        return (empresa.Id, centro.Id);
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

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
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
