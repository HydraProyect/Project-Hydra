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
        var reclamacion = new ReclamacionBuzonIntegracion(" CAE@Tenant.com ", TenantId, ConexionId, AhoraUtc);

        reclamacion.BuzonEmail.Should().Be("cae@tenant.com");
        reclamacion.TenantPropietarioId.Should().Be(TenantId);
        reclamacion.ConexionIntegracionId.Should().Be(ConexionId);
        reclamacion.ReclamadoEnUtc.Should().Be(AhoraUtc);
    }

    [Fact]
    public void Rechaza_un_buzon_vacio()
    {
        var accion = () => new ReclamacionBuzonIntegracion(" ", TenantId, ConexionId, AhoraUtc);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_buzon_que_supera_la_longitud_maxima()
    {
        var buzonDemasiadoLargo = new string('a', ReclamacionBuzonIntegracion.LongitudMaximaBuzonEmail + 1) + "@tenant.com";

        var accion = () => new ReclamacionBuzonIntegracion(buzonDemasiadoLargo, TenantId, ConexionId, AhoraUtc);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_tenant_propietario_vacio()
    {
        var accion = () => new ReclamacionBuzonIntegracion("cae@tenant.com", Guid.Empty, ConexionId, AhoraUtc);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_una_conexion_vacia()
    {
        var accion = () => new ReclamacionBuzonIntegracion("cae@tenant.com", TenantId, Guid.Empty, AhoraUtc);

        accion.Should().Throw<ArgumentException>();
    }
}
