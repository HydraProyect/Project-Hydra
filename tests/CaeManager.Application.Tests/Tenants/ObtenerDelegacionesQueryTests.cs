using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Domain.Tenants;
using FluentAssertions;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Hallazgo 16 de la revisión 1 a 1 del 2026-10-21: en el contexto de un Tenant beneficiario
/// delegado, <c>/delegaciones</c> enseñaba todas las delegaciones del Operador CAE externo hacia
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

    private Task<IReadOnlyList<DelegacionDto>> ConsultarAsync(Guid? tenantAfectadoId) =>
        new ObtenerDelegacionesQueryHandler(_contexto, new Sesion(_operador.Id))
            .Handle(new ObtenerDelegacionesQuery(tenantAfectadoId), CancellationToken.None);

    [Fact]
    public async Task Con_un_Tenant_beneficiario_solo_salen_las_delegaciones_que_lo_afectan()
    {
        var delegaciones = await ConsultarAsync(_beneficiario.Id);

        delegaciones.Select(d => d.Id).Should().Equal([_haciaBeneficiario.Id],
            "la delegación hacia otra Empresa gestionada no afecta a este Tenant, y la de Soporte TALVEG no es del Operador CAE externo");
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
        // trae su delegación de soporte, porque el Tenant de origen no participa en ella.
        var delegaciones = await ConsultarAsync(_plataforma.Id);

        delegaciones.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, "GestorCae", "Administrador", "Administrador")]
    [InlineData(false, "GestorCae", "DireccionCae", "DireccionCae")]
    [InlineData(false, "CoordinadorCae", "GestorCae", "GestorCae")]
    [InlineData(false, "GestorCae", "Consulta", "Consulta")]
    [InlineData(false, "GestorCae", null, "GestorCae")]
    [InlineData(false, "GestorCae", "Trabajador", "GestorCae")]
    [InlineData(true, "Consulta", "Administrador", "Consulta")]
    public void El_rol_que_se_ensena_es_el_que_ejerce(bool esSoporte, string rolGuardado, string? rolDeOrigen, string esperado) =>
        TenantsBeneficiariosAutorizados.RolEjercidoEnDelegacion(esSoporte, rolGuardado, rolDeOrigen).Should().Be(esperado);

    private sealed class Sesion(Guid origen) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(origen);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
