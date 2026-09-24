using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// <b>El job de expiración de asignaciones, tal como corre en producción:</b>
/// conectado como <c>cae_app_runtime</c>, con el interceptor de sesión real y sin
/// usuario. Es la condición que el test anterior
/// (<see cref="CorreccionesRevisionF1Tests"/>) no reproducía: aquel conecta como
/// propietario de la base y sin interceptor, así que la política
/// <c>posicion_en_la_asignacion</c> no le ata y el job "funcionaba".
///
/// <para>
/// Bajo el rol restringido, sin <c>AmbitoTenantExplicito</c>, el interceptor fija
/// <c>app.tenant_id</c> y <c>app.tenant_origen_id</c> a cadena vacía y el job ve
/// cero filas: las Asignaciones vencidas siguen Vigentes (y ocupan los índices
/// únicos parciales, 23505 al dar de alta la sustituta) y las Programadas no se
/// activan nunca.
/// </para>
///
/// <para>
/// Nota sobre la migración <c>20260820230549_RlsCatalogosDeAsignacion</c>: su
/// comentario afirma que el job de expiración "opera como propietario". Era falso
/// desde que el tráfico de la aplicación pasó a <c>cae_app_runtime</c>; la
/// migración ya está aplicada y no se reescribe, así que la corrección queda
/// aquí y en el propio job.
/// </para>
///
/// Todos los casos usan una Asignación de Operación <b>externa</b>: el Tenant
/// operador (un Operador CAE externo) no es el Tenant propietario. Es el caso en
/// que <c>app.tenant_id</c> y <c>app.tenant_origen_id</c> divergen, y el que
/// demuestra que recorrer por Tenant propietario basta.
/// </summary>
public class ExpiracionAsignacionesBajoRlsTests
{
    [Fact]
    public async Task Bajo_el_rol_de_runtime_cierra_las_vencidas_y_activa_las_programadas_de_cada_Tenant_propietario()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        var ahora = DateTime.UtcNow;
        var hace3h = ahora.AddHours(-3);

        var propietarioA = new Tenant("Propietario A expiración", PerfilVocabularioTenant.ClienteDirecto);
        var propietarioC = new Tenant("Propietario C expiración", PerfilVocabularioTenant.ClienteDirecto);
        var propietarioSuspendido = new Tenant("Propietario suspendido expiración", PerfilVocabularioTenant.ClienteDirecto);
        propietarioSuspendido.Suspender();
        var operadorExterno = new Tenant("Operador CAE externo expiración", PerfilVocabularioTenant.Consultora);

        // Asignación de Operación Vigente con la vigencia ya pasada: propietario A,
        // operada por el Operador CAE externo.
        var operacionVencida = AsignacionOperacion.Externa(
            propietarioA.Id, operadorExterno.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            vigenciaDesde: hace3h, vigenciaHasta: ahora.AddHours(-1), ahora: hace3h);

        // Asignación de Cartera Programada cuya vigencia ya empezó, sobre una
        // operación vigente del propietario C operada por el mismo Operador CAE externo.
        var operacionDeC = AsignacionOperacion.Externa(
            propietarioC.Id, operadorExterno.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            vigenciaDesde: hace3h, vigenciaHasta: null, ahora: hace3h);
        var carteraProgramada = AsignacionCartera.Externa(
            operacionDeC, usuarioId: Guid.NewGuid(), rol: Roles.GestorCae, AmbitoAsignacion.Universal,
            vigenciaDesde: ahora.AddHours(-1), vigenciaHasta: null, ahora: hace3h);

        // La vigencia de una asignación no deja de correr porque su Tenant
        // propietario esté suspendido: el job anterior (como propietario de la
        // base) no distinguía estados, y este tampoco.
        var operacionVencidaDeSuspendido = AsignacionOperacion.Externa(
            propietarioSuspendido.Id, operadorExterno.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            vigenciaDesde: hace3h, vigenciaHasta: ahora.AddHours(-1), ahora: hace3h);

        operacionVencida.Estado.Should().Be(EstadoAsignacion.Vigente, "precondición de la siembra");
        carteraProgramada.Estado.Should().Be(EstadoAsignacion.Programada, "precondición de la siembra");

        await using (var propietarioBd = CrearContextoPropietario(arnes.CadenaPropietario))
        {
            propietarioBd.Tenants.AddRange(propietarioA, propietarioC, propietarioSuspendido, operadorExterno);
            propietarioBd.AsignacionesOperacion.AddRange(operacionVencida, operacionDeC, operacionVencidaDeSuspendido);
            propietarioBd.AsignacionesCartera.Add(carteraProgramada);
            await propietarioBd.SaveChangesAsync();
        }

        var job = new ExpiracionAsignacionesHostedService(
            arnes.Servicios.GetRequiredService<IServiceScopeFactory>(),
            new LiderSiempre(),
            NullLogger<ExpiracionAsignacionesHostedService>.Instance);

        await job.ProcesarAsync(CancellationToken.None);

        await using var verificacion = CrearContextoPropietario(arnes.CadenaPropietario);

        var vencida = await verificacion.AsignacionesOperacion.AsNoTracking().SingleAsync(o => o.Id == operacionVencida.Id);
        vencida.Estado.Should().Be(EstadoAsignacion.Cerrada,
            "la vigencia terminó hace una hora; bajo cae_app_runtime el job tiene que verla y cerrarla");
        vencida.MotivoCierre.Should().Be(MotivoCierreAsignacion.Expirada);

        var programada = await verificacion.AsignacionesCartera.AsNoTracking().SingleAsync(c => c.Id == carteraProgramada.Id);
        programada.Estado.Should().Be(EstadoAsignacion.Vigente,
            "su vigencia empezó hace una hora y su operación está vigente");

        var deSuspendido = await verificacion.AsignacionesOperacion.AsNoTracking()
            .SingleAsync(o => o.Id == operacionVencidaDeSuspendido.Id);
        deSuspendido.Estado.Should().Be(EstadoAsignacion.Cerrada,
            "la suspensión del Tenant propietario no congela la vigencia de sus asignaciones");

        var operacionVigente = await verificacion.AsignacionesOperacion.AsNoTracking().SingleAsync(o => o.Id == operacionDeC.Id);
        operacionVigente.Estado.Should().Be(EstadoAsignacion.Vigente, "sin fecha de fin, no se toca");
    }

    /// <summary>
    /// Contexto como propietario de la base, sin interceptores: siembra y lectura
    /// de verificación con visión global, fuera de la política.
    /// </summary>
    private static CaeManagerDbContext CrearContextoPropietario(string cadenaPropietario)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = null };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
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
