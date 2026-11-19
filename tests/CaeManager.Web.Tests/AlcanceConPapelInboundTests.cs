using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El alcance de <see cref="AlcanceDatosService"/> según el papel activo (B-02b y B-03). Con papel Inbound, y solo dentro
/// de una petición de Inbound (<see cref="AmbitoPeticionInbound"/>), los perfiles de organización del Operador CAE externo alcanzan el Tenant TCE entero, como en Outbound (decisión de Chris del
/// 2026-11-16): la vía de Operación Inbound solo les da papel con una operación del Tenant entero. Con una operación que
/// ya no autoriza, ningún perfil tiene acceso total ni cartera, aunque el rol se resolviera un instante antes. La cartera
/// Inbound del Gestor CAE se prueba bajo PostgreSQL en <c>FuncionAmparoInboundBajoRuntimeTests</c>. Estas pruebas deciden
/// antes de leer la base: el contexto apunta a un servidor que no existe, así que una consulta las haría fallar.
/// </summary>
public class AlcanceConPapelInboundTests
{
    private static readonly Guid TenantTce = Guid.NewGuid();

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("Consulta")]
    public async Task Con_papel_Inbound_los_perfiles_de_organizacion_alcanzan_el_Tenant_entero_dentro_de_una_peticion_de_Inbound(string rol)
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario(rol, PapelActivo.Inbound));

        using (AmbitoPeticionInbound.Abrir())
            (await alcance.TieneAccesoTotalAsync()).Should().BeTrue();
    }

    /// <summary>
    /// Fuera de una petición de Inbound (Trabajadores, Documentos, Empresas… de Outbound), el papel Inbound no da alcance a
    /// ningún perfil: el papel Inbound no abre los lectores de Outbound del Tenant TCE (revisor-puente Opus, ronda 1 de B-03).
    /// </summary>
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("Consulta")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public async Task Con_papel_Inbound_fuera_de_una_peticion_de_Inbound_ningun_perfil_tiene_alcance(string rol)
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario(rol, PapelActivo.Inbound));

        (await alcance.TieneAccesoTotalAsync()).Should().BeFalse();
        (await alcance.AlcanzaElTenantEnteroAsync()).Should().BeFalse();
        (await alcance.ObtenerClienteIdsVisiblesAsync()).Should().BeEmpty();
    }

    /// <summary>Una misma instancia, dentro y fuera de una petición de Inbound, en los dos órdenes.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lo_memoizado_dentro_de_una_peticion_de_Inbound_no_vale_fuera_ni_al_reves(bool dentroPrimero)
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario("Administrador", PapelActivo.Inbound));

        async Task<bool> Dentro()
        {
            using (AmbitoPeticionInbound.Abrir())
                return await alcance.TieneAccesoTotalAsync();
        }

        if (dentroPrimero)
        {
            (await Dentro()).Should().BeTrue();
            (await alcance.TieneAccesoTotalAsync()).Should().BeFalse("fuera no reutiliza el acceso total de dentro");
        }
        else
        {
            (await alcance.TieneAccesoTotalAsync()).Should().BeFalse();
            (await Dentro()).Should().BeTrue("dentro no reutiliza el cero de fuera");
        }
    }

    [Fact]
    public async Task Con_papel_Inbound_el_Coordinador_CAE_por_operacion_alcanza_el_Tenant_entero()
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario("CoordinadorCae", PapelActivo.Inbound));

        using (AmbitoPeticionInbound.Abrir())
            (await alcance.AlcanzaElTenantEnteroAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("Consulta")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public async Task Una_operacion_que_ya_no_autoriza_no_da_acceso_total_ni_cartera(string rol)
    {
        // El rol se resolvió con la operación todavía vigente; el papel, un instante después, ya no la encuentra.
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario(rol, PapelActivo.OperacionQueYaNoAutoriza));

        (await alcance.TieneAccesoTotalAsync()).Should().BeFalse();
        (await alcance.AlcanzaElTenantEnteroAsync()).Should().BeFalse();
        (await alcance.ObtenerClienteIdsVisiblesAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Una misma instancia (un ámbito DI: la petición o el circuito) recorre el Tenant en el fan-out, con papel Outbound, y
    /// lo lee después en el contexto seleccionado, con una operación que ya no autoriza, y al revés: lo memoizado en un lado
    /// no vale en el otro (Codex, ronda 2 de la PR #2293).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lo_memoizado_en_el_fan_out_no_se_reutiliza_en_el_contexto_seleccionado_ni_al_reves(bool fanOutPrimero)
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new UsuarioConPapelSegunAmbito("Administrador"));

        async Task<bool> EnElFanOut()
        {
            using (AmbitoTenantExplicito.Establecer(TenantTce))
                return await alcance.TieneAccesoTotalAsync();
        }

        if (fanOutPrimero)
        {
            (await EnElFanOut()).Should().BeTrue("control: en el fan-out, Outbound, el Administrador tiene acceso total");
            (await alcance.TieneAccesoTotalAsync()).Should().BeFalse("no reutiliza el acceso total del fan-out");
        }
        else
        {
            (await alcance.TieneAccesoTotalAsync()).Should().BeFalse();
            (await EnElFanOut()).Should().BeTrue("el fan-out no reutiliza el cero del contexto seleccionado");
        }
    }

    /// <summary>
    /// Un «sin alcance» memoizado (una operación que dejó de autorizar un instante) no dura más que la generación: pasada la
    /// caducidad, con la operación de nuevo vigente, el alcance vuelve (Codex, ronda 2 de B-03).
    /// </summary>
    [Fact]
    public async Task Sin_alcance_memoizado_caduca_con_la_generacion()
    {
        await using var contexto = ContextoSinServidor();
        var usuario = new UsuarioConPapelMutable("Administrador", PapelActivo.OperacionQueYaNoAutoriza);
        var reloj = new RelojAvanzable();
        var alcance = new AlcanceDatosService(contexto, usuario, new TenantActualAmbiental { TenantId = TenantTce },
            new SinSesionPrivilegiada(), reloj: reloj);

        (await alcance.TieneAccesoTotalAsync()).Should().BeFalse("control: con la operación que ya no autoriza");
        usuario.Papel = PapelActivo.Outbound;
        (await alcance.TieneAccesoTotalAsync()).Should().BeFalse("dentro de la generación sigue lo memoizado");

        reloj.Avanzar(TimeSpan.FromSeconds(61));
        (await alcance.TieneAccesoTotalAsync()).Should().BeTrue("caducada la generación, el papel se vuelve a leer");
    }
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Control_con_papel_Outbound_el_mismo_perfil_tiene_acceso_total(string rol)
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario(rol, PapelActivo.Outbound));

        (await alcance.TieneAccesoTotalAsync()).Should().BeTrue();
    }

    /// <summary>
    /// «Inbound sin Outbound» (2026-11-18): al Tenant de origen solo Inbound, dentro de una petición de Outbound, ningún
    /// perfil tiene alcance (no hay <c>ServicioCae.Outbound</c>); fuera de ella (Núcleo) el Administrador TCE conserva el
    /// acceso total. Con <paramref name="soloInbound"/> en falso (Tenant histórico sin líneas o con las dos) no cambia nada.
    /// </summary>
    [Theory]
    [InlineData("Administrador", true)]
    [InlineData("DireccionCae", true)]
    [InlineData("Administrador", false)]
    [InlineData("DireccionCae", false)]
    public async Task Al_Tenant_de_origen_solo_Inbound_una_peticion_de_Outbound_no_le_da_alcance(string rol, bool soloInbound)
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario(rol, PapelActivo.TenantDeOrigen), Criterio(soloInbound));

        using (AmbitoPeticionOutbound.Fijar(true))
            (await alcance.TieneAccesoTotalAsync()).Should().Be(!soloInbound);
        (await alcance.TieneAccesoTotalAsync()).Should().BeTrue("fuera de una petición de Outbound el Núcleo no cambia");
    }

    /// <summary>
    /// Hallazgo de la segunda opinión sobre la PR #2379: el badge de Visitas de <c>/centros</c> (página del Núcleo) usa
    /// <c>ObtenerProximaVisitaPorCentroQuery</c>, que es de Outbound. Con el alcance real dentro de la petición de Outbound,
    /// al Tenant de origen solo Inbound no le llega ningún Centro y el handler responde vacío sin leer Visitas; al Tenant de
    /// origen con Outbound el alcance no restringe (y el handler lee todas, ver <c>ObtenerProximaVisitaPorCentroAlcanceTests</c>).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Al_Tenant_de_origen_solo_Inbound_el_badge_de_Visitas_no_le_da_ningun_Centro(bool soloInbound)
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario("Administrador", PapelActivo.TenantDeOrigen), Criterio(soloInbound));
        var handler = new CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro
            .ObtenerProximaVisitaPorCentroQueryHandler(new VisitasQueNoSeLeen(), alcance);

        using (AmbitoPeticionOutbound.Fijar(true))
        {
            var centros = await alcance.ObtenerCentroIdsVisiblesAsync();
            if (soloInbound)
            {
                centros.Should().BeEmpty();
                (await handler.Handle(new([Guid.NewGuid()]), CancellationToken.None)).Should().BeEmpty(
                    "sin ningún Centro visible el handler no llega a leer Visitas");
            }
            else
            {
                centros.Should().BeNull("el Tenant de origen con Outbound conserva su alcance sin restricción");
            }
        }
    }

    /// <summary>Unas Visitas que no se pueden leer: si el handler llegara a consultarlas, el test falla.</summary>
    private sealed class VisitasQueNoSeLeen : CaeManager.Application.Visitas.IVisitasQueryContext
    {
        public IQueryable<CaeManager.Domain.Visitas.Visita> Visitas =>
            throw new InvalidOperationException("el handler no debía leer Visitas");
        public IQueryable<CaeManager.Domain.Visitas.VisitaTrabajador> VisitasTrabajadores =>
            throw new InvalidOperationException("el handler no debía leer VisitasTrabajadores");
    }

    private static CriterioDeOutbound Criterio(bool soloInbound) => new(new ServiceCollection()
        .AddSingleton<ITenantActual>(new TenantActualAmbiental { TenantId = TenantTce })
        .AddSingleton<CaeManager.Application.Comercial.LineaOutbound.ITenantSoloInbound>(new SoloInboundFijo(soloInbound))
        .BuildServiceProvider());

    private sealed class SoloInboundFijo(bool soloInbound) : CaeManager.Application.Comercial.LineaOutbound.ITenantSoloInbound
    {
        public Task<bool> EsSoloInboundAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(soloInbound);
    }

    private static AlcanceDatosService Alcance(
        CaeManagerDbContext contexto, ICurrentUserService usuario, ICriterioDeOutbound? criterio = null) =>
        new(contexto, usuario, new TenantActualAmbiental { TenantId = TenantTce }, new SinSesionPrivilegiada(),
            criterioDeOutbound: criterio);

    /// <summary>Un contexto que nunca conecta: el puerto 1 de localhost no tiene ningún PostgreSQL.</summary>
    private static CaeManagerDbContext ContextoSinServidor()
    {
        var tenant = new TenantActualAmbiental { TenantId = TenantTce };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=ninguna;Timeout=1")
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    /// <summary>Una persona de un Operador CAE externo que entra por la Asignación de Operación de su organización.</summary>
    private sealed class Usuario(string rol, PapelActivo papel) : ICurrentUserService, IEncargoDeAdministracionActual
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
        public Task<PapelActivo> ObtenerPapelActivoAsync() => Task.FromResult(papel);
        public Task<bool> OperaPorAsignacionDeOperacionAsync() => Task.FromResult(true);
        public Task<Guid?> EncargoQueElevaAsync() => Task.FromResult<Guid?>(null);
        public Task<Guid?> EncargoPlenoAsync() => Task.FromResult<Guid?>(null);
        public Guid? EncargoDeLaUltimaResolucion(Guid asignacionOperacionId) => null;
        public bool EncargoPlenoDeLaUltimaResolucion(Guid asignacionOperacionId) => false;
    }

    /// <summary>Papel Outbound en el ámbito explícito del fan-out y, fuera, una operación seleccionada que ya no autoriza.</summary>
    private sealed class UsuarioConPapelSegunAmbito(string rol) : ICurrentUserService, IEncargoDeAdministracionActual
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
        public Task<PapelActivo> ObtenerPapelActivoAsync() =>
            Task.FromResult(AmbitoTenantExplicito.TenantIdActual is null ? PapelActivo.OperacionQueYaNoAutoriza : PapelActivo.Outbound);
        public Task<bool> OperaPorAsignacionDeOperacionAsync() => Task.FromResult(true);
        public Task<Guid?> EncargoQueElevaAsync() => Task.FromResult<Guid?>(null);
        public Task<Guid?> EncargoPlenoAsync() => Task.FromResult<Guid?>(null);
        public Guid? EncargoDeLaUltimaResolucion(Guid asignacionOperacionId) => null;
        public bool EncargoPlenoDeLaUltimaResolucion(Guid asignacionOperacionId) => false;
    }

    private sealed class UsuarioConPapelMutable(string rol, PapelActivo papel) : ICurrentUserService
    {
        public PapelActivo Papel { get; set; } = papel;
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
        public Task<PapelActivo> ObtenerPapelActivoAsync() => Task.FromResult(Papel);
    }

    /// <summary>Reloj de marcas de tiempo que solo avanza cuando el test lo pide.</summary>
    private sealed class RelojAvanzable : TimeProvider
    {
        private long _marca = 1_000_000;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _marca;
        public void Avanzar(TimeSpan cuanto) => _marca += cuanto.Ticks;
    }
    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}
