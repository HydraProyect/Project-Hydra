using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

public class ReclamacionBuzonIntegracionTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ConexionId = Guid.NewGuid();
    private static readonly DateTime AhoraUtc = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Se_crea_con_el_buzon_normalizado_a_minusculas()
    {
        var reclamacion = new ReclamacionBuzonIntegracion(" CAE@Tenant.com ", null, TenantId, ConexionId, AhoraUtc);

        reclamacion.BuzonEmail.Should().Be("cae@tenant.com");
        reclamacion.TenantPropietarioId.Should().Be(TenantId);
        reclamacion.ConexionIntegracionId.Should().Be(ConexionId);
        reclamacion.ReclamadoEnUtc.Should().Be(AhoraUtc);
    }

    [Fact]
    public void Rechaza_un_buzon_vacio()
    {
        var accion = () => new ReclamacionBuzonIntegracion(" ", null, TenantId, ConexionId, AhoraUtc);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_buzon_que_supera_la_longitud_maxima()
    {
        var buzonDemasiadoLargo = new string('a', ReclamacionBuzonIntegracion.LongitudMaximaBuzonEmail + 1) + "@tenant.com";

        var accion = () => new ReclamacionBuzonIntegracion(buzonDemasiadoLargo, null, TenantId, ConexionId, AhoraUtc);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_tenant_propietario_vacio()
    {
        var accion = () => new ReclamacionBuzonIntegracion("cae@tenant.com", null, Guid.Empty, ConexionId, AhoraUtc);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_una_conexion_vacia()
    {
        var accion = () => new ReclamacionBuzonIntegracion("cae@tenant.com", null, TenantId, Guid.Empty, AhoraUtc);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Guarda_la_identidad_del_buzon_y_no_la_cambia_por_la_de_otro()
    {
        var reclamacion = new ReclamacionBuzonIntegracion("cae@tenant.com", "inbox-1", TenantId, Guid.NewGuid(), AhoraUtc);

        reclamacion.IdentidadBuzon.Should().Be("inbox-1");
        reclamacion.FijarIdentidadBuzon("inbox-1");
        var accion = () => reclamacion.FijarIdentidadBuzon("inbox-2");

        accion.Should().Throw<InvalidOperationException>();
        reclamacion.IdentidadBuzon.Should().Be("inbox-1");
    }

    [Fact]
    public void Una_reclamacion_antigua_sin_identidad_la_recibe_despues()
    {
        var reclamacion = new ReclamacionBuzonIntegracion("cae@tenant.com", null, TenantId, Guid.NewGuid(), AhoraUtc);

        reclamacion.FijarIdentidadBuzon("inbox-1");

        reclamacion.IdentidadBuzon.Should().Be("inbox-1");
    }

    [Fact]
    public void Rechaza_una_identidad_demasiado_larga()
    {
        var accion = () => new ReclamacionBuzonIntegracion(
            "cae@tenant.com", new string('a', ReclamacionBuzonIntegracion.LongitudMaximaIdentidadBuzon + 1), TenantId, Guid.NewGuid(), AhoraUtc);

        accion.Should().Throw<ArgumentException>();
    }
}
