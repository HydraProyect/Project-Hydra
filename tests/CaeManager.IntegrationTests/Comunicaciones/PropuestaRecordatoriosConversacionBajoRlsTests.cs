using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// <b>La propuesta de recordatorio a los dos días laborables de espera de respuesta externa (decisión del 2026-10-17), tal
/// como corre en producción:</b> conectado como <c>cae_app_runtime</c>, con los interceptores reales de sesión RLS, sellado y
/// auditoría, y sin usuario. Mismo arnés que <see cref="CierreConversacionesResueltasBajoRlsTests"/>. Prueba que la tabla
/// nueva <c>PropuestasRecordatorioConversacion</c> existe con su RLS y su FK compuesta, que el proceso ve las filas de cada
/// Tenant propietario (también el suspendido), que no repite la propuesta de una espera y que descarta sola la que dejó de
/// aplicar. La siembra va como propietario de la base, porque no es lo que se mide.
/// </summary>
public class PropuestaRecordatoriosConversacionBajoRlsTests
{
    [Fact]
    public async Task Bajo_el_rol_de_runtime_propone_una_vez_por_espera_en_cada_Tenant_y_descarta_la_que_ya_no_aplica()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false, serviciosAdicionales: ConPlazos);
        var ahora = DateTime.UtcNow;

        var tenantA = new Tenant("Tenant propietario con conversaciones en espera");
        var tenantSuspendido = new Tenant("Tenant propietario suspendido con una espera larga");
        tenantSuspendido.Suspender();

        Conversacion esperaLarga, esperaReciente, abiertaVieja, esperaDelSuspendido;
        await using (var propietarioBd = CrearContextoPropietario(arnes.CadenaPropietario, new TenantActualPorAmbito()))
        {
            propietarioBd.Tenants.AddRange(tenantA, tenantSuspendido);
            await propietarioBd.SaveChangesAsync();

            using (AmbitoTenantExplicito.Establecer(tenantA.Id))
            {
                esperaLarga = Sembrar("Esperando desde hace diez días", EstadoConversacion.EsperandoRespuestaExterna, ahora.AddDays(-10));
                esperaReciente = Sembrar("Esperando desde ayer", EstadoConversacion.EsperandoRespuestaExterna, ahora.AddDays(-1));
                abiertaVieja = Sembrar("Abierta desde hace semanas", EstadoConversacion.Abierta, ahora.AddDays(-30));
                propietarioBd.Conversaciones.AddRange(esperaLarga, esperaReciente, abiertaVieja);
                await propietarioBd.SaveChangesAsync();
            }

            using (AmbitoTenantExplicito.Establecer(tenantSuspendido.Id))
            {
                esperaDelSuspendido = Sembrar("Esperando desde hace doce días", EstadoConversacion.EsperandoRespuestaExterna, ahora.AddDays(-12));
                propietarioBd.Conversaciones.Add(esperaDelSuspendido);
                await propietarioBd.SaveChangesAsync();
            }
        }

        var job = new PropuestaRecordatoriosConversacionHostedService(
            arnes.Servicios.GetRequiredService<IServiceScopeFactory>(),
            new LiderSiempre(),
            _registro);

        await job.ProcesarAsync(ahora, CancellationToken.None);
        await job.ProcesarAsync(ahora.AddHours(1), CancellationToken.None);
        _registro.Errores.Should().BeEmpty("un fallo por Tenant se registra y se sigue; aquí no debe haber ninguno");

        await using (var verificacion = CrearContextoPropietario(arnes.CadenaPropietario, new TenantActualPorAmbito()))
        {
            var propuestas = await verificacion.PropuestasRecordatorioConversacion.IgnoreQueryFilters().AsNoTracking().ToListAsync();
            var deEsteTest = propuestas.Where(p => new[]
                { esperaLarga.Id, esperaReciente.Id, abiertaVieja.Id, esperaDelSuspendido.Id }
                .Contains(p.ConversacionId)).ToList();

            deEsteTest.Select(p => p.ConversacionId).Should().BeEquivalentTo([esperaLarga.Id, esperaDelSuspendido.Id],
                "solo las esperas de correo de más de dos días laborables; el segundo pase no repite la propuesta de la misma espera");
            deEsteTest.Should().OnlyContain(p => p.Estado == EstadoPropuestaRecordatorio.Pendiente, "se propone; nada se envía solo");
            deEsteTest.Single(p => p.ConversacionId == esperaLarga.Id).TenantId.Should().Be(tenantA.Id,
                "la propuesta vive en el Tenant propietario de la conversación");
            deEsteTest.Single(p => p.ConversacionId == esperaDelSuspendido.Id).TenantId.Should().Be(tenantSuspendido.Id);

            var idPropuesta = deEsteTest.Single(p => p.ConversacionId == esperaLarga.Id).Id;
            (await verificacion.RegistrosAuditoria.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(r => r.EntidadId == idPropuesta && r.TipoActor == TipoActorAuditoria.Sistema))
                .Should().BeTrue("la propuesta queda auditada como acción del Sistema, sin persona detrás");
        }

        // Llega la respuesta externa: la conversación se reabre y la propuesta Pendiente se descarta sola en el pase siguiente.
        using (AmbitoTenantExplicito.Establecer(tenantA.Id))
        {
            using var ambito = arnes.Servicios.CreateScope();
            var runtime = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var cargada = (await new ConversacionRepository(runtime).ObtenerPorIdAsync(esperaLarga.Id))!;
            cargada.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "empresa@contraparte.es",
                "<p>Ya lo tenéis.</p>", ahora.AddHours(1));
            await runtime.SaveChangesAsync();
        }

        await job.ProcesarAsync(ahora.AddHours(2), CancellationToken.None);

        await using var final = CrearContextoPropietario(arnes.CadenaPropietario, new TenantActualPorAmbito());
        (await final.PropuestasRecordatorioConversacion.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(p => p.ConversacionId == esperaLarga.Id))
            .Estado.Should().Be(EstadoPropuestaRecordatorio.Descartada, "la espera terminó al llegar la respuesta");
        (await final.PropuestasRecordatorioConversacion.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(p => p.ConversacionId == esperaDelSuspendido.Id))
            .Estado.Should().Be(EstadoPropuestaRecordatorio.Pendiente, "la otra espera sigue igual");
    }

    /// <summary>
    /// La FK compuesta (ConversacionId, TenantId) impide colgar una propuesta del Tenant A de una conversación del Tenant B,
    /// y la RLS no deja ver a un Tenant las propuestas de otro. Se exige el código de violación de FK (23503) y no una
    /// excepción cualquiera.
    /// </summary>
    [Fact]
    public async Task La_propuesta_no_cruza_Tenants_ni_por_la_FK_ni_por_la_RLS()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false, serviciosAdicionales: ConPlazos);
        var ahora = DateTime.UtcNow;
        var tenantA = new Tenant("Tenant propietario A de la propuesta");
        var tenantB = new Tenant("Tenant propietario B ajeno");
        Conversacion espera;

        await using (var propietarioBd = CrearContextoPropietario(arnes.CadenaPropietario, new TenantActualPorAmbito()))
        {
            propietarioBd.Tenants.AddRange(tenantA, tenantB);
            await propietarioBd.SaveChangesAsync();
            using (AmbitoTenantExplicito.Establecer(tenantA.Id))
            {
                espera = Sembrar("Esperando respuesta", EstadoConversacion.EsperandoRespuestaExterna, ahora.AddDays(-10));
                propietarioBd.Conversaciones.Add(espera);
                await propietarioBd.SaveChangesAsync();
            }
        }

        await new PropuestaRecordatoriosConversacionHostedService(
                arnes.Servicios.GetRequiredService<IServiceScopeFactory>(), new LiderSiempre(),
                _registro)
            .ProcesarAsync(ahora, CancellationToken.None);
        _registro.Errores.Should().BeEmpty();

        using (AmbitoTenantExplicito.Establecer(tenantB.Id))
        {
            using var ambito = arnes.Servicios.CreateScope();
            var runtimeB = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            (await runtimeB.PropuestasRecordatorioConversacion.IgnoreQueryFilters().AnyAsync(p => p.ConversacionId == espera.Id))
                .Should().BeFalse("la RLS no deja ver al Tenant B la propuesta del Tenant A, ni saltándose el filtro de EF");
        }

        await using var propietario = CrearContextoPropietario(arnes.CadenaPropietario, new TenantActualPorAmbito());
        var idPropuesta = await propietario.PropuestasRecordatorioConversacion.IgnoreQueryFilters()
            .Where(p => p.ConversacionId == espera.Id).Select(p => p.Id).SingleAsync();
        var excepcion = await Record.ExceptionAsync(() => propietario.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"PropuestasRecordatorioConversacion\" SET \"TenantId\" = {tenantB.Id} WHERE \"Id\" = {idPropuesta}"));
        excepcion.Should().BeOfType<Npgsql.PostgresException>()
            .Which.SqlState.Should().Be("23503", "la FK compuesta ata la propuesta al Tenant propietario de su conversación");
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
    private readonly RegistroDeErrores _registro = new();

    /// <summary>El proceso registra el fallo de un Tenant y sigue: sin esto el test solo vería «no hay propuestas».</summary>
    private sealed class RegistroDeErrores : ILogger<PropuestaRecordatoriosConversacionHostedService>
    {
        public List<string> Errores { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Errores.Add(formatter(state, exception) + " " + exception);
        }
    }
    /// <summary>
    /// El arnés no registra el servicio de plazos (en producción lo hace InfrastructureServiceCollectionExtensions). Aquí el
    /// Operador CAE no se resuelve y el proceso cae al calendario por defecto, que es justo la rama que cubre este test;
    /// la resolución del Operador CAE la prueban los tests del SLA.
    /// </summary>
    private static void ConPlazos(IServiceCollection servicios)
    {
        servicios.AddScoped<IContextoOperativoConversacionService, ContextoOperativoSinOperador>();
        servicios.AddSingleton(Options.Create(new CalendariosOperadorCaeOptions()));
        servicios.AddScoped<PlazosConversacionService>();
    }

    private sealed class ContextoOperativoSinOperador : IContextoOperativoConversacionService
    {
        public Task<OperacionDeConversacion?> ResolverAsync(Guid propietarioTenantId, Guid? clienteEmpresarialId, Guid? empresaId,
            CancellationToken cancellationToken) => Task.FromResult<OperacionDeConversacion?>(null);

        public Task AvisarCriticaAsync(OperacionDeConversacion operacion, Guid eventoId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
