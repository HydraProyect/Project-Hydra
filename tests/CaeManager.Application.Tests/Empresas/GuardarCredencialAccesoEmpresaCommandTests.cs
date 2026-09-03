using CaeManager.Application.Empresas.Commands.GuardarCredencialAccesoEmpresa;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;
using EmpresaRepositorioFalso = CaeManager.Application.Tests.Documentos.EmpresaRepositorioFalso;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// DEC-62: desde que el formulario deja de precargar la contraseña, un
/// campo vacío/null en una edición ya no puede significar "bórrala" — la
/// conserva. Este handler es la única pieza que materializa esa semántica.
/// </summary>
public class GuardarCredencialAccesoEmpresaCommandTests
{
    /// <summary>Cubre null Y cadena vacía por separado: el handler decide con IsNullOrEmpty, no con "is null".</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Editar_con_contrasena_vacia_conserva_la_almacenada(string? contrasenaEnviada)
    {
        var empresa = new Empresa("Empresa propia S.L.");
        var empresas = new EmpresaRepositorioFalso();
        empresas.Agregar(empresa);
        var credenciales = new CredencialAccesoEmpresaRepositorioFalso();
        credenciales.Agregar(new CredencialAccesoEmpresa(
            empresa.Id, "https://portal.example", "campo", "usuario-viejo", "secreta-almacenada", "notas-viejas"));
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new GuardarCredencialAccesoEmpresaCommandHandler(
            empresas, credenciales, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id]), unitOfWork);

        var resultado = await handler.Handle(
            new GuardarCredencialAccesoEmpresaCommand(
                empresa.Id, "https://portal.nuevo.example", "campo", "usuario-nuevo", Contrasena: contrasenaEnviada, Notas: "notas-nuevas"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var credencial = credenciales.Credenciales.Single();
        credencial.Contrasena.Should().Be("secreta-almacenada", "un campo vacío/null ya no borra la contraseña (DEC-62)");
        credencial.UrlAcceso.Should().Be("https://portal.nuevo.example");
        credencial.Usuario.Should().Be("usuario-nuevo");
        credencial.Notas.Should().Be("notas-nuevas");
    }

    [Fact]
    public async Task Editar_con_contrasena_no_vacia_la_reemplaza()
    {
        var empresa = new Empresa("Empresa propia S.L.");
        var empresas = new EmpresaRepositorioFalso();
        empresas.Agregar(empresa);
        var credenciales = new CredencialAccesoEmpresaRepositorioFalso();
        credenciales.Agregar(new CredencialAccesoEmpresa(empresa.Id, null, null, null, "secreta-vieja"));
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new GuardarCredencialAccesoEmpresaCommandHandler(
            empresas, credenciales, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id]), unitOfWork);

        var resultado = await handler.Handle(
            new GuardarCredencialAccesoEmpresaCommand(empresa.Id, null, null, null, "secreta-nueva"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        credenciales.Credenciales.Single().Contrasena.Should().Be("secreta-nueva");
    }

    [Fact]
    public async Task Crear_con_contrasena_vacia_no_pone_una_contrasena()
    {
        var empresa = new Empresa("Empresa propia S.L.");
        var empresas = new EmpresaRepositorioFalso();
        empresas.Agregar(empresa);
        var credenciales = new CredencialAccesoEmpresaRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new GuardarCredencialAccesoEmpresaCommandHandler(
            empresas, credenciales, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id]), unitOfWork);

        var resultado = await handler.Handle(
            new GuardarCredencialAccesoEmpresaCommand(empresa.Id, "https://portal.example", null, "usuario", Contrasena: null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        credenciales.Credenciales.Single().Contrasena.Should().BeNull("todavía no había fila que conservar");
    }
}
