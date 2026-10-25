using CaeManager.Application.Documentos.Commands.GuardarSelloEmpresa;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class GuardarSelloEmpresaCommandHandlerTests
{
    private static readonly byte[] ImagenDePrueba = [1, 2, 3];

    private sealed class Contexto
    {
        public EmpresaRepositorioFalso Empresas { get; } = new();
        public SelloEmpresaRepositorioFalso Repositorio { get; } = new();
        public ConversorImagenSelloServiceFalso Conversor { get; } = new();
        public FileStorageServiceFalso Almacenamiento { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();
        public RegistroDeAvisos<GuardarSelloEmpresaCommandHandler> Registro { get; } = new();

        public GuardarSelloEmpresaCommandHandler CrearHandler(AlcanceDatosServiceFalso? alcance = null) =>
            new(Empresas, Repositorio, alcance ?? new AlcanceDatosServiceFalso(), Conversor, Almacenamiento,
                UnitOfWork, Registro);
    }

    [Fact]
    public async Task Falla_cuando_la_empresa_no_existe()
    {
        var contexto = new Contexto();
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new GuardarSelloEmpresaCommand(Guid.NewGuid(), ImagenDePrueba), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Empresa.NoEncontrada");
    }

    [Fact]
    public async Task Falla_cuando_la_empresa_esta_fuera_de_la_cartera()
    {
        var contexto = new Contexto();
        var empresa = new Empresa("Empresa de prueba", "B12345674");
        contexto.Empresas.Agregar(empresa);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false);
        var handler = contexto.CrearHandler(alcance);

        var resultado = await handler.Handle(new GuardarSelloEmpresaCommand(empresa.Id, ImagenDePrueba), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Empresa.NoEncontrada");
    }

    [Fact]
    public async Task Crea_el_sello_pidiendo_siempre_recorte_de_fondo()
    {
        var contexto = new Contexto();
        var empresa = new Empresa("Empresa de prueba", "B12345674");
        contexto.Empresas.Agregar(empresa);
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(new GuardarSelloEmpresaCommand(empresa.Id, ImagenDePrueba), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        contexto.Repositorio.Sellos.Should().ContainSingle(s => s.EmpresaId == empresa.Id);
        contexto.Conversor.UltimoRecortarFondoClaro.Should().BeTrue();
        contexto.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Reemplaza_el_sello_existente_en_vez_de_acumular_uno_nuevo()
    {
        var contexto = new Contexto();
        var empresa = new Empresa("Empresa de prueba", "B12345674");
        contexto.Empresas.Agregar(empresa);
        var handler = contexto.CrearHandler();
        await handler.Handle(new GuardarSelloEmpresaCommand(empresa.Id, ImagenDePrueba), CancellationToken.None);
        var urlPrimerSello = contexto.Repositorio.Sellos[0].ImagenUrl;

        await handler.Handle(new GuardarSelloEmpresaCommand(empresa.Id, [9, 9, 9]), CancellationToken.None);

        contexto.Repositorio.Sellos.Should().ContainSingle("una empresa tiene como mucho un sello guardado");
        contexto.Repositorio.Sellos[0].ImagenUrl.Should().NotBe(urlPrimerSello);
    }

    [Fact]
    public async Task El_aviso_de_la_imagen_anterior_que_no_se_pudo_borrar_identifica_la_empresa_sin_revelar_la_clave()
    {
        var contexto = new Contexto();
        var empresa = new Empresa("Empresa de prueba", "B12345674");
        contexto.Empresas.Agregar(empresa);
        var handler = contexto.CrearHandler();
        await handler.Handle(new GuardarSelloEmpresaCommand(empresa.Id, ImagenDePrueba), CancellationToken.None);
        var clavePrimerSello = contexto.Repositorio.Sellos[0].ImagenUrl;
        contexto.Almacenamiento.ExcepcionAlEliminar = new IOException($"Acceso denegado a {clavePrimerSello}");

        var resultado = await handler.Handle(new GuardarSelloEmpresaCommand(empresa.Id, [9, 9, 9]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("el sello nuevo ya está guardado: la limpieza del anterior no tapa el resultado");
        var aviso = contexto.Registro.Entradas.Should().ContainSingle().Subject;
        aviso.Nivel.Should().Be(LogLevel.Error);
        aviso.Texto.Should().Contain(empresa.Id.ToString()).And.Contain(nameof(IOException))
            .And.NotContain(clavePrimerSello).And.NotContain("Acceso denegado");
    }
}
