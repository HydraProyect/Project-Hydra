using CaeManager.Application.Documentos.Commands.GuardarFirmaGuardadaUsuario;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class GuardarFirmaGuardadaUsuarioCommandHandlerTests
{
    private static readonly Guid UsuarioId = Guid.NewGuid();
    private static readonly byte[] ImagenDePrueba = [1, 2, 3];

    private sealed class Contexto
    {
        public FirmaGuardadaUsuarioRepositorioFalso Repositorio { get; } = new();
        public ConversorImagenSelloServiceFalso Conversor { get; } = new();
        public FileStorageServiceFalso Almacenamiento { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();
        public RegistroDeAvisos<GuardarFirmaGuardadaUsuarioCommandHandler> Registro { get; } = new();

        public GuardarFirmaGuardadaUsuarioCommandHandler CrearHandler(bool sinUsuario = false) =>
            new(Repositorio, Conversor, Almacenamiento, new CurrentUserServiceFalso(sinUsuario ? null : UsuarioId),
                UnitOfWork, Registro);
    }

    [Fact]
    public async Task Falla_cuando_no_se_puede_identificar_al_usuario_actual()
    {
        var contexto = new Contexto();
        var handler = contexto.CrearHandler(sinUsuario: true);

        var resultado = await handler.Handle(
            new GuardarFirmaGuardadaUsuarioCommand(ImagenDePrueba, EsImagenDibujada: true), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("FirmaGuardadaUsuario.SinUsuario");
    }

    [Fact]
    public async Task Crea_la_firma_guardada_cuando_no_existe_todavia()
    {
        var contexto = new Contexto();
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new GuardarFirmaGuardadaUsuarioCommand(ImagenDePrueba, EsImagenDibujada: true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        contexto.Repositorio.Firmas.Should().ContainSingle(f => f.UsuarioId == UsuarioId);
        contexto.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Imagen_dibujada_no_pide_recorte_de_fondo()
    {
        var contexto = new Contexto();
        var handler = contexto.CrearHandler();

        await handler.Handle(new GuardarFirmaGuardadaUsuarioCommand(ImagenDePrueba, EsImagenDibujada: true), CancellationToken.None);

        contexto.Conversor.UltimoRecortarFondoClaro.Should().BeFalse();
    }

    [Fact]
    public async Task Imagen_subida_pide_recorte_de_fondo()
    {
        var contexto = new Contexto();
        var handler = contexto.CrearHandler();

        await handler.Handle(new GuardarFirmaGuardadaUsuarioCommand(ImagenDePrueba, EsImagenDibujada: false), CancellationToken.None);

        contexto.Conversor.UltimoRecortarFondoClaro.Should().BeTrue();
    }

    [Fact]
    public async Task Reemplaza_la_firma_existente_en_vez_de_acumular_una_nueva()
    {
        var contexto = new Contexto();
        var handler = contexto.CrearHandler();
        await handler.Handle(new GuardarFirmaGuardadaUsuarioCommand(ImagenDePrueba, EsImagenDibujada: true), CancellationToken.None);
        var urlPrimeraFirma = contexto.Repositorio.Firmas[0].ImagenUrl;

        await handler.Handle(new GuardarFirmaGuardadaUsuarioCommand([9, 9, 9], EsImagenDibujada: true), CancellationToken.None);

        contexto.Repositorio.Firmas.Should().ContainSingle("un usuario tiene como mucho una firma guardada");
        contexto.Repositorio.Firmas[0].ImagenUrl.Should().NotBe(urlPrimeraFirma);
    }

    [Fact]
    public async Task El_aviso_de_la_imagen_anterior_que_no_se_pudo_borrar_identifica_al_usuario_sin_revelar_la_clave()
    {
        var contexto = new Contexto();
        var handler = contexto.CrearHandler();
        await handler.Handle(new GuardarFirmaGuardadaUsuarioCommand(ImagenDePrueba, EsImagenDibujada: true), CancellationToken.None);
        var clavePrimeraFirma = contexto.Repositorio.Firmas[0].ImagenUrl;
        contexto.Almacenamiento.ExcepcionAlEliminar = new IOException($"Acceso denegado a {clavePrimeraFirma}");

        var resultado = await handler.Handle(
            new GuardarFirmaGuardadaUsuarioCommand([9, 9, 9], EsImagenDibujada: true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("la firma nueva ya está guardada: la limpieza de la anterior no tapa el resultado");
        var aviso = contexto.Registro.Entradas.Should().ContainSingle().Subject;
        aviso.Nivel.Should().Be(LogLevel.Error);
        aviso.Texto.Should().Contain(UsuarioId.ToString()).And.Contain(nameof(IOException))
            .And.NotContain(clavePrimeraFirma).And.NotContain("Acceso denegado");
    }
}
