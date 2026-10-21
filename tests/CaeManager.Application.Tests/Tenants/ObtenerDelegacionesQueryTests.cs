using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Hallazgo 16 de la revisiÃ³n 1 a 1 del 2026-10-21: en el contexto de un Tenant beneficiario
/// delegado, <c>/delegaciones</c> enseÃ±aba todas las delegaciones del Operador CAE externo hacia
/// otras Empresas gestionadas. <see cref="ObtenerDelegacionesQuery.TenantAfectadoId"/> estrecha
/// la lista a las que tienen ese Tenant en un extremo, sin soltar nunca el Tenant de origen.
/// </summary>
public class ObtenerDelegacionesQueryTests
{
    private readonly TenantsQueryContextFalso _contexto = new();
    private readonly Tenant _operador = new("Operador CAE externo de prueba", PerfilVocabularioTenant.Consultora);
    private readonly Tenant _beneficiario = new("Empresa gestionada A");
    private readonly Tenant _otraGestionada = new("Empresa gestionada B");
    private readonly Tenant _plataforma = new("Plataforma de prueba", esPlataforma: true);
    private readonly DelegacionTenant _haciaBeneficiario;
    private readonly DelegacionTenant _haciaOtra;
    private readonly DelegacionTenant _soporteAjenoSobreBeneficiario;

    public ObtenerDelegacionesQueryTests()
    {
        _contexto.ListaTenants.AddRange([_operador, _beneficiario, _otraGestionada, _plataforma]);
        _haciaBeneficiario = new DelegacionTenant(_operador.Id, _beneficiario.Id);
        _haciaOtra = new DelegacionTenant(_operador.Id, _otraGestionada.Id);
        // Soporte TALVEG sobre el mismo Tenant beneficiario: no es del Operador CAE externo.
        _soporteAjenoSobreBeneficiario = DelegacionTenant.ParaSoporte(_plataforma.Id, _beneficiario.Id);
        _contexto.ListaDelegacionesTenant.AddRange([_haciaBeneficiario, _haciaOtra, _soporteAjenoSobreBeneficiario]);
    }

    private readonly List<AsignacionOperacion> _operaciones = [];

    private Task<IReadOnlyList<DelegacionDto>> ConsultarAsync(Guid? tenantAfectadoId, Guid? origen = null) =>
        new ObtenerDelegacionesQueryHandler(_contexto, new Sesion(origen ?? _operador.Id), new Operaciones(_operaciones))
            .Handle(new ObtenerDelegacionesQuery(tenantAfectadoId), CancellationToken.None);

    [Fact]
    public async Task Con_el_Tenant_seleccionado_en_el_extremo_del_Operador_CAE_tambien_sale()
    {
        // Visto desde el Tenant beneficiario: su Operador CAE externo es el otro extremo.
        var delegaciones = await ConsultarAsync(_operador.Id, origen: _beneficiario.Id);

        delegaciones.Select(d => d.Id).Should().Equal([_haciaBeneficiario.Id],
            "la de Soporte TALVEG sobre el mismo Tenant no tiene al Operador CAE externo en ningÃºn extremo");
    }

    [Fact]
    public async Task Marca_la_delegacion_con_operacion_vigente_del_Tenant_entero()
    {
        var ahora = DateTime.UtcNow;
        _operaciones.Add(AsignacionOperacion.Externa(
            _beneficiario.Id, _operador.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));

        var delegaciones = await ConsultarAsync(null);

        delegaciones.Single(d => d.Id == _haciaBeneficiario.Id).OperacionDelTenantEntero.Should().BeTrue();
        delegaciones.Single(d => d.Id == _haciaOtra.Id).OperacionDelTenantEntero.Should().BeFalse(
            "sin operaciÃ³n sobre esa Empresa gestionada el perfil de origen no es el rol que se ejerce");
    }

    [Fact]
    public async Task Con_un_Tenant_beneficiario_solo_salen_las_delegaciones_que_lo_afectan()
    {
        var delegaciones = await ConsultarAsync(_beneficiario.Id);

        delegaciones.Select(d => d.Id).Should().Equal([_haciaBeneficiario.Id],
            "la delegaciÃ³n hacia otra Empresa gestionada no afecta a este Tenant, y la de Soporte TALVEG no es del Operador CAE externo");
    }

    [Fact]
    public async Task En_la_organizacion_propia_salen_todas_las_del_Tenant_de_origen()
    {
        var delegaciones = await ConsultarAsync(null);

        delegaciones.Select(d => d.Id).Should().BeEquivalentTo([_haciaBeneficiario.Id, _haciaOtra.Id]);
    }

    [Fact]
    public async Task Un_Tenant_sin_relacion_con_el_origen_no_trae_nada()
    {
        // La coordenada de contexto no es autoridad: nombrar el Tenant de plataforma no
        // trae su delegaciÃ³n de soporte, porque el Tenant de origen no participa en ella.
        var delegaciones = await ConsultarAsync(_plataforma.Id);

        delegaciones.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, true, "GestorCae", "Administrador", "Administrador")]
    [InlineData(false, true, "GestorCae", "DireccionCae", "DireccionCae")]
    [InlineData(false, true, "GestorCae", "Consulta", "Consulta")]
    [InlineData(false, false, "GestorCae", "Administrador", "GestorCae")]
    [InlineData(false, false, "CoordinadorCae", "GestorCae", "GestorCae")]
    [InlineData(false, true, "GestorCae", null, "GestorCae")]
    [InlineData(false, true, "GestorCae", "Trabajador", "GestorCae")]
    [InlineData(true, true, "Consulta", "Administrador", "Consulta")]
    public void El_rol_que_se_ensena_es_el_que_ejerce(
        bool esSoporte, bool operacionDelTenantEntero, string rolGuardado, string? rolDeOrigen, string esperado) =>
        TenantsBeneficiariosAutorizados.RolEjercidoEnTenantBeneficiario(esSoporte, operacionDelTenantEntero, rolGuardado, rolDeOrigen)
            .Should().Be(esperado);

    private sealed class Operaciones(List<AsignacionOperacion> operaciones) : IOperacionesQueryContext
    {
        public IQueryable<AsignacionOperacion> AsignacionesOperacion =>
            new TestAsyncQueryable<AsignacionOperacion>(operaciones.AsQueryable());

        public IQueryable<AsignacionCartera> AsignacionesCartera =>
            new TestAsyncQueryable<AsignacionCartera>(new List<AsignacionCartera>().AsQueryable());
    }

    private sealed class Sesion(Guid origen) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(origen);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
