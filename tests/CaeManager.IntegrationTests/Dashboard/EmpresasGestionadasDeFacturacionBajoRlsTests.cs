using CaeManager.Application.Common;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Domain.Common;
using CaeManager.Domain.Dashboard;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Dashboard;

/// <summary>
/// La fuente única de las Empresas gestionadas que lee <c>/facturacion</c>
/// (<see cref="ObtenerEmpresasGestionadasDelMesQuery"/>, la misma del Cuadro de dirección) con el cableado de producción
/// (<see cref="ArnesDeArranqueRuntime"/>: conexión como <c>cae_app_runtime</c> y sus interceptores), para que la RLS de las
/// Asignaciones de Operación sea la que se mide y no solo el filtro del handler.
///
/// <para>
/// Escenario: el Operador CAE A opera a la Empresa X (Asignación de Operación externa vigente) y tiene su Asignación raíz
/// sobre sí mismo; el Operador CAE B opera a la Empresa Y. Un Coordinador CAE del Operador A ve las Empresas gestionadas
/// (unidades) pero nunca importes: las tarifas son plano Comercial y solo las lee el Administrador o la Dirección CAE.
/// El Tenant del propio Operador nunca figura como Empresa gestionada, ni la raíz lo cuela, y una Empresa gestionada no es Operador CAE.
/// </para>
/// </summary>
public class EmpresasGestionadasDeFacturacionBajoRlsTests : IAsyncLifetime
{
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly UsuarioConmutable _sesion = new();
    private ArnesDeArranqueRuntime _arnes = null!;

    private Guid _operadorA;
    private Guid _operadorB;
    private Guid _empresaX;
    private Guid _empresaY;
    private int _anyo;
    private int _mes;

    public async Task InitializeAsync()
    {
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false, currentUserServicePersonalizado: _sesion);

        // La siembra va como propietario de las tablas: la Asignación de Operación la crea el alta, no el Operador.
        var tenantActual = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_arnes.CadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        await using var propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);

        var a = new Tenant("Operador A");
        var b = new Tenant("Operador B");
        var x = new Tenant("Empresa X");
        var y = new Tenant("Empresa Y");
        propietario.Tenants.AddRange(a, b, x, y);
        await propietario.SaveChangesAsync();
        (_operadorA, _operadorB, _empresaX, _empresaY) = (a.Id, b.Id, x.Id, y.Id);

        var ahora = DateTime.UtcNow;
        var hoy = DiaDeNegocio.Hoy();
        (_anyo, _mes) = (hoy.Year, hoy.Month);

        using (AmbitoTenantExplicito.Establecer(_operadorA))
        {
            propietario.AsignacionesOperacion.Add(AsignacionOperacion.Raiz(_operadorA, ServicioCae.Outbound, ahora.AddDays(-400), ahora));
            propietario.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
                _empresaX, _operadorA, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-2), null, ahora));
        }

        using (AmbitoTenantExplicito.Establecer(_operadorB))
            propietario.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
                _empresaY, _operadorB, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-2), null, ahora));
        await propietario.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    [Fact]
    public async Task Un_Coordinador_CAE_ve_las_Empresas_gestionadas_pero_nunca_importes()
    {
        // Control positivo: el Administrador del Operador A fija una tarifa a la Empresa X y la lee.
        Como("Administrador", _operadorA);
        (await FijarTarifaAsync(_empresaX)).EsExitoso.Should().BeTrue();
        (await LeerTarifasAsync()).Should().ContainSingle("control positivo: el Administrador del Operador A ve su tarifa");

        Como("CoordinadorCae", _operadorA);

        var gestionadas = await LeerGestionadasAsync(_anyo, _mes);
        gestionadas.EsOperadorCae.Should().BeTrue();
        gestionadas.Tramos.Keys.Should().BeEquivalentTo([_empresaX], "las unidades las ve quien lee el Cuadro de dirección");
        gestionadas.Tramos[_empresaX].Should().NotBeEmpty();

        (await LeerTarifasAsync()).Should().BeNull("sin Administrador o Dirección CAE no hay tarifas que ver, aunque existan");
        (await AutorizacionTarifasOperadorCae.OperadorQueGestionaAsync(_sesion)).Should().BeNull(
            "es la guarda que deja a null las líneas con importe de la serie que consume /facturacion");
    }

    [Fact]
    public async Task El_propio_Tenant_del_Operador_nunca_aparece_ni_con_su_Asignacion_raiz_ni_el_de_otro_Operador()
    {
        foreach (var rol in new[] { "CoordinadorCae", "DireccionCae" })
        {
            Como(rol, _operadorA);
            var deA = await LeerGestionadasAsync(_anyo, _mes);

            deA.Tramos.Keys.Should().BeEquivalentTo([_empresaX], $"{rol} del Operador A: solo la Empresa X");
            deA.Tramos.Keys.Should().NotContain(_operadorA, "la raíz no cuenta: el Operador no se factura a sí mismo");
            deA.Tramos.Keys.Should().NotContain(_empresaY, "la Empresa Y la opera el Operador B");
        }

        Como("DireccionCae", _operadorB);
        var deB = await LeerGestionadasAsync(_anyo, _mes);
        deB.Tramos.Keys.Should().BeEquivalentTo([_empresaY]);
        deB.Tramos.Keys.Should().NotContain([_operadorB, _empresaX]);

        // Un mes anterior a toda vigencia: sigue siendo Operador CAE, sin ninguna Empresa gestionada que cobrar.
        Como("DireccionCae", _operadorA);
        var antes = await LeerGestionadasAsync(_anyo - 1, _mes);
        antes.EsOperadorCae.Should().BeTrue();
        antes.Tramos.Should().BeEmpty();
    }

    [Fact]
    public async Task La_Empresa_gestionada_no_es_Operador_CAE_y_la_base_solo_entrega_las_filas_de_cada_Tenant()
    {
        Como("DireccionCae", _empresaX);
        var deX = await LeerGestionadasAsync(_anyo, _mes);
        deX.EsOperadorCae.Should().BeFalse("la Empresa gestionada no opera a nadie: no lee a su Operador");
        deX.Tramos.Should().BeEmpty();

        // La base, sin ayuda del filtro global: la política por TenantId decide qué Asignaciones entrega cada Tenant.
        var deA = await LeerAsignacionesComoRuntimeAsync(_operadorA);
        deA.Should().OnlyContain(a => a.Operador == _operadorA, "la base no entrega al Operador A ninguna fila de otro Tenant");
        deA.Should().Contain((_empresaX, _operadorA, false), "control positivo: su Asignación externa sobre X");
        (await LeerAsignacionesComoRuntimeAsync(_operadorB)).Should().BeEquivalentTo([(_empresaY, _operadorB, false)]);
        // La Empresa gestionada solo ve las filas de las que es propietaria (quién la opera): ninguna del Operador B ni de la Empresa Y.
        (await LeerAsignacionesComoRuntimeAsync(_empresaX)).Should().OnlyContain(a => a.Propietario == _empresaX);
    }

    private void Como(string? rol, Guid tenantOrigen)
    {
        _sesion.UsuarioId = _usuario;
        _sesion.Rol = rol;
        _sesion.TenantOrigenId = tenantOrigen;
    }

    private async Task<EmpresasGestionadasDelMesDto> LeerGestionadasAsync(int anyo, int mes)
    {
        await using var scope = _arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        return await new ObtenerEmpresasGestionadasDelMesQueryHandler(_sesion, contexto, new SinAccesoTotal())
            .Handle(new ObtenerEmpresasGestionadasDelMesQuery(anyo, mes), CancellationToken.None);
    }

    private async Task<IReadOnlyList<TarifaOperadorCaeDto>?> LeerTarifasAsync()
    {
        await using var scope = _arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        return await new ObtenerTarifasOperadorCaeQueryHandler(_sesion, contexto)
            .Handle(new ObtenerTarifasOperadorCaeQuery(), CancellationToken.None);
    }

    private async Task<Result> FijarTarifaAsync(Guid empresa)
    {
        await using var scope = _arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var handler = new FijarTarifaOperadorCaeCommandHandler(_sesion, contexto, contexto, contexto, TimeProvider.System);
        return await handler.Handle(
            new FijarTarifaOperadorCaeCommand(empresa, ConceptoTarifaOperadorCae.CentroNuevo, 50m, "EUR", true), CancellationToken.None);
    }

    /// <summary>Las Asignaciones de Operación que entrega la base al Tenant dado, con el filtro global desactivado.</summary>
    private async Task<List<(Guid Propietario, Guid Operador, bool EsRaiz)>> LeerAsignacionesComoRuntimeAsync(Guid tenant)
    {
        // La sesión es la de una persona de ese Tenant (su Tenant de origen): la RLS se mide con su identidad efectiva.
        Como("DireccionCae", tenant);
        await using var scope = _arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using (AmbitoTenantExplicito.Establecer(tenant))
            return (await contexto.AsignacionesOperacion.IgnoreQueryFilters()
                .Select(a => new { a.PropietarioTenantId, a.OperadorTenantId, a.EsRaiz }).ToListAsync())
                .Select(a => (a.PropietarioTenantId, a.OperadorTenantId, a.EsRaiz)).ToList();
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>La persona de la sesión, cambiable entre pasos: el interceptor la lee en cada apertura de conexión.</summary>
    private sealed class UsuarioConmutable : ICurrentUserService
    {
        public Guid? UsuarioId { get; set; }
        public string? Rol { get; set; }
        public Guid? TenantOrigenId { get; set; }

        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(UsuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(Rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(Rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(TenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>Sin Sesión Privilegiada: aquí se entra siempre por rol de negocio.</summary>
    private sealed class SinAccesoTotal : IPoliticaNotaInterna
    {
        public Task<bool> PuedeLeerAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> TieneAccesoTotalAlTenantAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
