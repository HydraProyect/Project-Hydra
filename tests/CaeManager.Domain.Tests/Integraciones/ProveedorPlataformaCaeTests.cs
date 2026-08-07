using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

public class ProveedorPlataformaCaeTests
{
    [Fact]
    public void Se_crea_activo_por_defecto_con_el_codigo_normalizado_en_minusculas()
    {
        var proveedor = new ProveedorPlataformaCae("  Dokify  ", "Dokify", "Once For All");

        proveedor.Codigo.Should().Be("dokify");
        proveedor.Nombre.Should().Be("Dokify");
        proveedor.Grupo.Should().Be("Once For All");
        proveedor.Activo.Should().BeTrue();
    }

    [Fact]
    public void Se_puede_sembrar_inactivo()
    {
        var proveedor = new ProveedorPlataformaCae("arch", "Arch", activo: false);

        proveedor.Activo.Should().BeFalse();
    }

    [Fact]
    public void Grupo_vacio_se_normaliza_a_null()
    {
        var proveedor = new ProveedorPlataformaCae("ucae", "UCAE", "   ");

        proveedor.Grupo.Should().BeNull();
    }

    [Fact]
    public void Rechaza_un_codigo_vacio()
    {
        var accion = () => new ProveedorPlataformaCae(" ", "Dokify");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_nombre_vacio()
    {
        var accion = () => new ProveedorPlataformaCae("dokify", " ");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Activar_y_Desactivar_alternan_el_estado()
    {
        var proveedor = new ProveedorPlataformaCae("dokify", "Dokify");

        proveedor.Desactivar();
        proveedor.Activo.Should().BeFalse();

        proveedor.Activar();
        proveedor.Activo.Should().BeTrue();
    }
}

public class DominioProveedorPlataformaCaeTests
{
    [Fact]
    public void Se_crea_con_el_dominio_normalizado_en_minusculas()
    {
        var dominio = new DominioProveedorPlataformaCae(Guid.NewGuid(), "  Nalandaglobal.COM  ");

        dominio.Dominio.Should().Be("nalandaglobal.com");
    }

    [Fact]
    public void Rechaza_un_proveedor_vacio()
    {
        var accion = () => new DominioProveedorPlataformaCae(Guid.Empty, "nalandaglobal.com");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_dominio_vacio()
    {
        var accion = () => new DominioProveedorPlataformaCae(Guid.NewGuid(), " ");

        accion.Should().Throw<ArgumentException>();
    }
}
