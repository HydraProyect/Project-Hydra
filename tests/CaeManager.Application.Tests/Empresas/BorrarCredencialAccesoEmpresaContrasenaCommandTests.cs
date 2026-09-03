using CaeManager.Application.Empresas.Commands.BorrarCredencialAccesoEmpresaContrasena;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;
using EmpresaRepositorioFalso = CaeManager.Application.Tests.Documentos.EmpresaRepositorioFalso;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>DEC-62: el único acto que borra una contraseña, ahora que un campo vacío al guardar la conserva.</summary>
public class BorrarCredencialAccesoEmpresaContrasenaCommandTests
{
    [Fact]
    public async Task Borra_la_contrasena_sin_tocar_el_resto_de_campos()
    {
        var empresa = new Empresa("Empresa propia S.L.");
        var empresas = new EmpresaRepositorioFalso();
        empresas.Agregar(empresa);
        var credenciales = new CredencialAccesoEmpresaRepositorioFalso();
        credenciales.Agregar(new CredencialAccesoEmpresa(
            empresa.Id, "https://portal.example", "campo", "usuario", "secreta", "notas"));
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new BorrarCredencialAccesoEmpresaContrasenaCommandHandler(
            empresas, credenciales, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id]), unitOfWork);

        var resultado = await handler.Handle(new BorrarCredencialAccesoEmpresaContrasenaCommand(empresa.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var credencial = credenciales.Credenciales.Single();
        credencial.Contrasena.Should().BeNull();
        credencial.UrlAcceso.Should().Be("https://portal.example");
        credencial.Usuario.Should().Be("usuario");
        credencial.Notas.Should().Be("notas");
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Sin_credencial_existente_es_un_no_op_exitoso()
    {
        var empresa = new Empresa("Empresa propia S.L.");
        var empresas = new EmpresaRepositorioFalso();
        empresas.Agregar(empresa);
        var credenciales = new CredencialAccesoEmpresaRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new BorrarCredencialAccesoEmpresaContrasenaCommandHandler(
            empresas, credenciales, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id]), unitOfWork);

        var resultado = await handler.Handle(new BorrarCredencialAccesoEmpresaContrasenaCommand(empresa.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        credenciales.Credenciales.Should().BeEmpty("no había nada que borrar ni nada que crear");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Fuera_de_cartera_de_gestion_no_borra_nada()
    {
        var empresa = new Empresa("Empresa ajena S.L.");
        var empresas = new EmpresaRepositorioFalso();
        empresas.Agregar(empresa);
        var credenciales = new CredencialAccesoEmpresaRepositorioFalso();
        credenciales.Agregar(new CredencialAccesoEmpresa(empresa.Id, null, null, null, "secreta"));
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new BorrarCredencialAccesoEmpresaContrasenaCommandHandler(
            empresas, credenciales, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: []), unitOfWork);

        var resultado = await handler.Handle(new BorrarCredencialAccesoEmpresaContrasenaCommand(empresa.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Empresa.NoEncontrada");
        credenciales.Credenciales.Single().Contrasena.Should().Be("secreta", "la denegación no debe tocar nada");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// REC-153/DEC-62: un usuario de portal tiene la Empresa en su cartera de
    /// LECTURA (por eso ve su documentación) pero no en la de GESTIÓN — y
    /// borrar una credencial es un acto de gestión.
    /// </summary>
    [Fact]
    public async Task Usuario_de_portal_no_puede_borrar_la_contrasena_de_su_contratista()
    {
        var contratista = new Empresa("Contratista de mi Cliente S.L.");
        var empresas = new EmpresaRepositorioFalso();
        empresas.Agregar(contratista);
        var credenciales = new CredencialAccesoEmpresaRepositorioFalso();
        credenciales.Agregar(new CredencialAccesoEmpresa(contratista.Id, null, null, null, "secreta"));
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new BorrarCredencialAccesoEmpresaContrasenaCommandHandler(
            empresas, credenciales,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [contratista.Id], empresaIdsParaGestion: []),
            unitOfWork);

        var resultado = await handler.Handle(new BorrarCredencialAccesoEmpresaContrasenaCommand(contratista.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        credenciales.Credenciales.Single().Contrasena.Should().Be("secreta");
    }
}
