using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Centros;

/// <summary>
/// Papel por Centro (DECISION_LOG, 2026-11-08): el ancla <see cref="Centro.RelacionEmpresarialEntradaId"/> apunta
/// a la Relación Empresarial Empresa del Centro → X, contratista principal distinta del titular. El titular sigue
/// en <see cref="Centro.ClienteId"/>.
/// </summary>
public class CentroPapelDeEntradaTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Titular = Guid.NewGuid();
    private static readonly Guid EmpresaDelCentro = Guid.NewGuid();
    private static readonly Guid ContratistaPrincipal = Guid.NewGuid();

    private static T EnTenant<T>(T entidad, Guid tenant) where T : EntidadConTenant
    {
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(entidad, tenant);
        return entidad;
    }

    private static Centro NuevoCentro() => EnTenant(new Centro(Titular, EmpresaDelCentro, "Planta Zaragoza"), Tenant);

    private static RelacionEmpresarial Relacion(Guid proveedora, Guid cliente, Guid? tenant = null) =>
        EnTenant(RelacionEmpresarial.Crear(proveedora, cliente, DateTime.UtcNow), tenant ?? Tenant);

    [Fact]
    public void Un_centro_nuevo_entra_como_contratista_principal_sin_ancla()
    {
        NuevoCentro().RelacionEmpresarialEntradaId.Should().BeNull();
    }

    [Fact]
    public void Anclar_la_relacion_con_la_contratista_principal_la_fija_y_el_titular_no_cambia()
    {
        var centro = NuevoCentro();
        var relacion = Relacion(EmpresaDelCentro, ContratistaPrincipal);

        var resultado = centro.AnclarRelacionDeEntrada(relacion);

        resultado.EsExitoso.Should().BeTrue();
        centro.RelacionEmpresarialEntradaId.Should().Be(relacion.Id);
        centro.ClienteId.Should().Be(Titular);
    }

    [Fact]
    public void Una_relacion_de_otro_Tenant_se_rechaza_como_inexistente()
    {
        var centro = NuevoCentro();

        var resultado = centro.AnclarRelacionDeEntrada(
            Relacion(EmpresaDelCentro, ContratistaPrincipal, tenant: Guid.NewGuid()));

        resultado.Error.Codigo.Should().Be("Centro.RelacionEntradaNoEncontrada");
        centro.RelacionEmpresarialEntradaId.Should().BeNull();
    }

    [Fact]
    public void Una_relacion_cerrada_se_rechaza()
    {
        var centro = NuevoCentro();
        var relacion = Relacion(EmpresaDelCentro, ContratistaPrincipal);
        relacion.Cerrar(DateTime.UtcNow);

        centro.AnclarRelacionDeEntrada(relacion).Error.Codigo.Should().Be("Centro.RelacionEntradaNoVigente");
        centro.RelacionEmpresarialEntradaId.Should().BeNull();
    }

    [Fact]
    public void Una_relacion_de_otra_Empresa_se_rechaza()
    {
        var centro = NuevoCentro();

        centro.AnclarRelacionDeEntrada(Relacion(Guid.NewGuid(), ContratistaPrincipal))
            .Error.Codigo.Should().Be("Centro.RelacionEntradaDeOtraEmpresa");
        centro.RelacionEmpresarialEntradaId.Should().BeNull();
    }

    [Fact]
    public void Con_el_titular_como_cliente_entra_como_contratista_principal_y_no_hay_ancla()
    {
        var centro = NuevoCentro();

        centro.AnclarRelacionDeEntrada(Relacion(EmpresaDelCentro, Titular))
            .Error.Codigo.Should().Be("Centro.RelacionEntradaConElTitular");
        centro.RelacionEmpresarialEntradaId.Should().BeNull();
    }

    [Fact]
    public void Quitar_el_ancla_vuelve_a_contratista_principal()
    {
        var centro = NuevoCentro();
        centro.AnclarRelacionDeEntrada(Relacion(EmpresaDelCentro, ContratistaPrincipal));

        centro.QuitarAnclaDeEntrada();

        centro.RelacionEmpresarialEntradaId.Should().BeNull();
    }

    /// <summary>
    /// Quitar renueva la versión haya o no ancla: si solo la renovase al borrar un ancla, releer la versión tras
    /// quitar distinguiría un Centro con ancla cerrada de uno sin ancla (DECISION_LOG, 2026-11-11, punto 11 b).
    /// </summary>
    [Fact]
    public void Quitar_renueva_la_version_tambien_sin_ancla()
    {
        var conAncla = NuevoCentro();
        conAncla.AnclarRelacionDeEntrada(Relacion(EmpresaDelCentro, ContratistaPrincipal));
        var sinAncla = NuevoCentro();
        var versionConAncla = conAncla.Version;
        var versionSinAncla = sinAncla.Version;

        conAncla.QuitarAnclaDeEntrada();
        sinAncla.QuitarAnclaDeEntrada();

        conAncla.Version.Should().NotBe(versionConAncla);
        sinAncla.Version.Should().NotBe(versionSinAncla);
        sinAncla.RelacionEmpresarialEntradaId.Should().BeNull();
    }
}
