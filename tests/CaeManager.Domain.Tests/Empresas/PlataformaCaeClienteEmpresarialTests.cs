using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Empresas;

/// <summary>
/// Plataforma CAE de un Cliente empresarial y acceso de cada Empresa a ella (caja «Plataforma CAE»
/// de Cliente 360, decisión del 2026-10-18).
/// </summary>
public class PlataformaCaeClienteEmpresarialTests
{
    private static readonly Guid ClienteEmpresarialId = Guid.NewGuid();
    private static readonly Guid EmpresaId = Guid.NewGuid();

    [Fact]
    public void Crear_recorta_y_deja_nulos_los_campos_opcionales_vacios()
    {
        var plataforma = new PlataformaCaeClienteEmpresarial(ClienteEmpresarialId, "  Nalanda  ", "  ", " ");

        plataforma.ClienteEmpresarialId.Should().Be(ClienteEmpresarialId);
        plataforma.Nombre.Should().Be("Nalanda");
        plataforma.UrlPortal.Should().BeNull();
        plataforma.CodigoCliente.Should().BeNull();
    }

    [Fact]
    public void Crear_sin_Cliente_empresarial_falla()
    {
        var crear = () => new PlataformaCaeClienteEmpresarial(Guid.Empty, "Nalanda", null, null);

        crear.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Sin_nombre_falla(string nombre)
    {
        var crear = () => new PlataformaCaeClienteEmpresarial(ClienteEmpresarialId, nombre, null, null);

        crear.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://portal.example")]
    [InlineData("portal.example")]
    [InlineData("data:text/html,hola")]
    public void Una_direccion_que_no_es_http_ni_https_se_rechaza(string url)
    {
        var crear = () => new PlataformaCaeClienteEmpresarial(ClienteEmpresarialId, "Nalanda", url, null);

        crear.Should().Throw<ArgumentException>("la dirección se pinta como enlace y no puede ejecutar nada");
    }

    [Theory]
    [InlineData("https://portal.example/cliente")]
    [InlineData("http://portal.example")]
    public void Una_direccion_http_o_https_se_acepta(string url)
    {
        var plataforma = new PlataformaCaeClienteEmpresarial(ClienteEmpresarialId, "Nalanda", url, "ACME");

        plataforma.UrlPortal.Should().Be(url);
        plataforma.CodigoCliente.Should().Be("ACME");
    }

    [Fact]
    public void Superar_las_longitudes_maximas_falla()
    {
        var largoNombre = () => new PlataformaCaeClienteEmpresarial(
            ClienteEmpresarialId, new string('n', PlataformaCaeClienteEmpresarial.LongitudMaximaNombre + 1), null, null);
        var largoCodigo = () => new PlataformaCaeClienteEmpresarial(
            ClienteEmpresarialId, "Nalanda", null, new string('c', PlataformaCaeClienteEmpresarial.LongitudMaximaCodigoCliente + 1));
        var largaUrl = () => new PlataformaCaeClienteEmpresarial(
            ClienteEmpresarialId, "Nalanda", "https://p.example/" + new string('u', PlataformaCaeClienteEmpresarial.LongitudMaximaUrlPortal), null);

        largoNombre.Should().Throw<ArgumentException>();
        largoCodigo.Should().Throw<ArgumentException>();
        largaUrl.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Actualizar_sustituye_los_tres_datos()
    {
        var plataforma = new PlataformaCaeClienteEmpresarial(ClienteEmpresarialId, "Nalanda", "https://a.example", "A");

        plataforma.Actualizar("Dokify", null, null);

        plataforma.Nombre.Should().Be("Dokify");
        plataforma.UrlPortal.Should().BeNull();
        plataforma.CodigoCliente.Should().BeNull();
    }

    [Fact]
    public void Un_acceso_exige_Cliente_empresarial_y_Empresa_distintos_y_no_vacios()
    {
        ((Action)(() => _ = new AccesoPlataformaCaeEmpresa(Guid.Empty, EmpresaId))).Should().Throw<ArgumentException>();
        ((Action)(() => _ = new AccesoPlataformaCaeEmpresa(ClienteEmpresarialId, Guid.Empty))).Should().Throw<ArgumentException>();
        ((Action)(() => _ = new AccesoPlataformaCaeEmpresa(EmpresaId, EmpresaId))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Un_acceso_nuevo_no_tiene_usuario_ni_contrasena()
    {
        var acceso = new AccesoPlataformaCaeEmpresa(ClienteEmpresarialId, EmpresaId);

        acceso.Usuario.Should().BeNull();
        acceso.Contrasena.Should().BeNull();
        acceso.ContrasenaCambiadaEnUtc.Should().BeNull();
        acceso.ContrasenaCambiadaPorUsuarioId.Should().BeNull();
    }

    [Fact]
    public void Cambiar_usuario_recorta_y_vacio_lo_quita()
    {
        var acceso = new AccesoPlataformaCaeEmpresa(ClienteEmpresarialId, EmpresaId);

        acceso.CambiarUsuario("  b12345678  ");
        acceso.Usuario.Should().Be("b12345678");

        acceso.CambiarUsuario("   ");
        acceso.Usuario.Should().BeNull();
    }

    [Fact]
    public void Cambiar_usuario_demasiado_largo_falla()
    {
        var acceso = new AccesoPlataformaCaeEmpresa(ClienteEmpresarialId, EmpresaId);

        var cambiar = () => acceso.CambiarUsuario(new string('u', AccesoPlataformaCaeEmpresa.LongitudMaximaUsuario + 1));

        cambiar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Cambiar_contrasena_anota_cuando_y_quien()
    {
        var acceso = new AccesoPlataformaCaeEmpresa(ClienteEmpresarialId, EmpresaId);
        var autor = Guid.NewGuid();
        var ahora = new DateTime(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc);

        acceso.CambiarContrasena("s3cr3ta", autor, ahora);

        acceso.Contrasena.Should().Be("s3cr3ta");
        acceso.ContrasenaCambiadaEnUtc.Should().Be(ahora);
        acceso.ContrasenaCambiadaPorUsuarioId.Should().Be(autor);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Cambiar_a_una_contrasena_vacia_falla_porque_vacia_no_significa_borrar(string? contrasena)
    {
        var acceso = new AccesoPlataformaCaeEmpresa(ClienteEmpresarialId, EmpresaId);

        var cambiar = () => acceso.CambiarContrasena(contrasena!, null, DateTime.UtcNow);

        cambiar.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Cambiar_a_una_contrasena_demasiado_larga_falla()
    {
        var acceso = new AccesoPlataformaCaeEmpresa(ClienteEmpresarialId, EmpresaId);

        var cambiar = () => acceso.CambiarContrasena(
            new string('x', AccesoPlataformaCaeEmpresa.LongitudMaximaContrasena + 1), null, DateTime.UtcNow);

        cambiar.Should().Throw<ArgumentException>();
    }
}
