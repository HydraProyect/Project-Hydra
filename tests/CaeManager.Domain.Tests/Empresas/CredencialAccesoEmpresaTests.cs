using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Empresas;

/// <summary>
/// Cubre <see cref="CredencialAccesoPortal"/> a través de una de sus dos
/// clases derivadas (P2 #27 de docs/business/MATURITY_REVIEW.md —
/// unificación de CredencialAccesoEmpresa/CredencialAccesoSubcontrata en una
/// base compartida): como la lógica vive entera en la base, probarla aquí
/// también cubre a CredencialAccesoSubcontrata sin duplicar el test.
/// </summary>
public class CredencialAccesoEmpresaTests
{
    [Fact]
    public void Se_crea_con_los_datos_informados()
    {
        var credencial = new CredencialAccesoEmpresa(
            Guid.NewGuid(), "https://portal.ejemplo.com", "12345", "usuario", "secreto", "notas");

        credencial.UrlAcceso.Should().Be("https://portal.ejemplo.com");
        credencial.CampoEmpresa.Should().Be("12345");
        credencial.Usuario.Should().Be("usuario");
        credencial.Contrasena.Should().Be("secreto");
        credencial.Notas.Should().Be("notas");
    }

    [Fact]
    public void Rechaza_un_id_de_empresa_vacio()
    {
        var accion = () => new CredencialAccesoEmpresa(Guid.Empty, null, null, null, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_usuario_mas_largo_que_el_maximo()
    {
        var accion = () => new CredencialAccesoEmpresa(
            Guid.NewGuid(), null, null, new string('a', CredencialAccesoPortal.LongitudMaximaUsuario + 1), null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Actualizar_reemplaza_todos_los_campos()
    {
        var credencial = new CredencialAccesoEmpresa(Guid.NewGuid(), "url-vieja", null, null, null);

        credencial.Actualizar("url-nueva", "campo", "usuario", "secreto", "notas");

        credencial.UrlAcceso.Should().Be("url-nueva");
        credencial.CampoEmpresa.Should().Be("campo");
        credencial.Usuario.Should().Be("usuario");
        credencial.Contrasena.Should().Be("secreto");
        credencial.Notas.Should().Be("notas");
    }
}
