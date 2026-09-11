using CaeManager.Application.Subcontratas.Commands.GuardarCredencialAccesoSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;
using EmpresaRepositorioFalso = CaeManager.Application.Tests.Documentos.EmpresaRepositorioFalso;

namespace CaeManager.Application.Tests.Subcontratas;

/// <summary>
/// DEC-62, gemela de <c>GuardarCredencialAccesoEmpresaCommandTests</c>: desde
/// que el formulario deja de precargar la contraseña de la subcontrata
/// (<c>ObtenerCredencialAccesoSubcontrataSinContrasenaQuery</c>), un campo
/// vacío/null en una edición ya no puede significar "bórrala" — la conserva.
/// Este handler es la única pieza que materializa esa semántica.
/// </summary>
public class GuardarCredencialAccesoSubcontrataCommandTests
{
    /// <summary>Cubre null Y cadena vacía por separado: el handler decide con IsNullOrEmpty, no con "is null".</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Editar_con_contrasena_vacia_conserva_la_almacenada(string? contrasenaEnviada)
    {
        var subcontrata = Empresa.CrearComoSubcontrata("Contrata propia S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        var subcontratas = new EmpresaRepositorioFalso();
        subcontratas.Agregar(subcontrata);
        var credenciales = new CredencialAccesoSubcontrataRepositorioFalso();
        credenciales.Agregar(new CredencialAccesoSubcontrata(
            subcontrata.Id, "https://portal.example", "campo", "usuario-viejo", "secreta-almacenada", "notas-viejas"));
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new GuardarCredencialAccesoSubcontrataCommandHandler(
            subcontratas, credenciales, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: [subcontrata.Id]), unitOfWork);

        var resultado = await handler.Handle(
            new GuardarCredencialAccesoSubcontrataCommand(
                subcontrata.Id, "https://portal.nuevo.example", "campo", "usuario-nuevo", Contrasena: contrasenaEnviada, Notas: "notas-nuevas"),
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
        var subcontrata = Empresa.CrearComoSubcontrata("Contrata propia S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        var subcontratas = new EmpresaRepositorioFalso();
        subcontratas.Agregar(subcontrata);
        var credenciales = new CredencialAccesoSubcontrataRepositorioFalso();
        credenciales.Agregar(new CredencialAccesoSubcontrata(subcontrata.Id, null, null, null, "secreta-vieja"));
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new GuardarCredencialAccesoSubcontrataCommandHandler(
            subcontratas, credenciales, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: [subcontrata.Id]), unitOfWork);

        var resultado = await handler.Handle(
            new GuardarCredencialAccesoSubcontrataCommand(subcontrata.Id, null, null, null, "secreta-nueva"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        credenciales.Credenciales.Single().Contrasena.Should().Be("secreta-nueva");
    }

    [Fact]
    public async Task Crear_con_contrasena_vacia_no_pone_una_contrasena()
    {
        var subcontrata = Empresa.CrearComoSubcontrata("Contrata propia S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        var subcontratas = new EmpresaRepositorioFalso();
        subcontratas.Agregar(subcontrata);
        var credenciales = new CredencialAccesoSubcontrataRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new GuardarCredencialAccesoSubcontrataCommandHandler(
            subcontratas, credenciales, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: [subcontrata.Id]), unitOfWork);

        var resultado = await handler.Handle(
            new GuardarCredencialAccesoSubcontrataCommand(subcontrata.Id, "https://portal.example", null, "usuario", Contrasena: null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        credenciales.Credenciales.Single().Contrasena.Should().BeNull("todavía no había fila que conservar");
    }
}
