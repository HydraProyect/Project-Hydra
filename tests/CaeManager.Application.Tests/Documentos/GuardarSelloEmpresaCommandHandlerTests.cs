using CaeManager.Application.Documentos.Commands.GuardarSelloEmpresa;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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

        public GuardarSelloEmpresaCommandHandler CrearHandler(AlcanceDatosServiceFalso? alcance = null) =>
            new(Empresas, Repositorio, alcance ?? new AlcanceDatosServiceFalso(), Conversor, Almacenamiento,
                UnitOfWork, NullLogger<GuardarSelloEmpresaCommandHandler>.Instance);
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
}
