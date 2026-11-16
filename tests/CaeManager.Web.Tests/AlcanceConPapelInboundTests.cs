using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

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

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Control_con_papel_Outbound_el_mismo_perfil_tiene_acceso_total(string rol)
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario(rol, PapelActivo.Outbound));

        (await alcance.TieneAccesoTotalAsync()).Should().BeTrue();
    }

    private static AlcanceDatosService Alcance(CaeManagerDbContext contexto, ICurrentUserService usuario) =>
        new(contexto, usuario, new TenantActualAmbiental { TenantId = TenantTce }, new SinSesionPrivilegiada());

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

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}
