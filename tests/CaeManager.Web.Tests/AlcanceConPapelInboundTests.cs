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
/// Con papel Inbound (B-02b, DN-21), <see cref="AlcanceDatosService"/> no da acceso total ni cartera a ningún perfil hasta
/// que B-03 calcule el alcance de Inbound: el acceso total por rol abriría también los datos de Outbound del Tenant TCE, y
/// la cartera de Outbound de una Gestora CAE que gestiona el mismo Tenant no vale para validar en Inbound. La decisión se
/// toma antes de leer la base: el contexto apunta a un servidor que no existe, así que cualquier consulta haría fallar el
/// test. Los controles con papel Outbound dejan ver que no es el rol el que cierra.
/// </summary>
public class AlcanceConPapelInboundTests
{
    private static readonly Guid TenantTce = Guid.NewGuid();

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("Consulta")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public async Task Con_papel_Inbound_ningun_perfil_tiene_acceso_total_ni_cartera(string rol)
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario(rol, PapelActivo.Inbound));

        (await alcance.TieneAccesoTotalAsync()).Should().BeFalse();
        (await alcance.AlcanzaElTenantEnteroAsync()).Should().BeFalse();
        (await alcance.ObtenerClienteIdsVisiblesAsync()).Should().BeEmpty();
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

    [Fact]
    public async Task Control_con_papel_Outbound_el_Coordinador_CAE_por_operacion_alcanza_el_Tenant_entero()
    {
        await using var contexto = ContextoSinServidor();
        var alcance = Alcance(contexto, new Usuario("CoordinadorCae", PapelActivo.Outbound));

        (await alcance.AlcanzaElTenantEnteroAsync()).Should().BeTrue();
    }

    private static AlcanceDatosService Alcance(CaeManagerDbContext contexto, Usuario usuario) =>
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

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}
