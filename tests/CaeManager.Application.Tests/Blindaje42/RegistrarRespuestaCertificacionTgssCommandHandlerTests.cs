using CaeManager.Application.Blindaje42.Commands.RegistrarRespuestaCertificacionTgss;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Domain.Blindaje42;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Blindaje42;

public class RegistrarRespuestaCertificacionTgssCommandHandlerTests
{
    private static readonly DateOnly Hoy = DateOnly.FromDateTime(DateTime.UtcNow);
    private static readonly Guid UsuarioId = Guid.NewGuid();

    private sealed class Contexto
    {
        public SolicitudCertificacionTgssRepositorioFalso Repositorio { get; } = new();
        public FileStorageServiceFalso Almacenamiento { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();

        public RegistrarRespuestaCertificacionTgssCommandHandler CrearHandler(
            AlcanceDatosServiceFalso? alcance = null, CurrentUserServiceFalso? currentUser = null) =>
            new(Repositorio, alcance ?? new AlcanceDatosServiceFalso(),
                currentUser ?? new CurrentUserServiceFalso(UsuarioId), Almacenamiento, UnitOfWork);
    }

    [Fact]
    public async Task Falla_cuando_la_solicitud_no_existe()
    {
        var contexto = new Contexto();
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new RegistrarRespuestaCertificacionTgssCommand(Guid.NewGuid(), ResultadoCertificacionTgss.SinDescubiertos, Hoy),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("CertificacionTgss.NoEncontrada");
    }

    [Fact]
    public async Task Falla_cuando_el_cliente_de_la_solicitud_esta_fuera_de_la_cartera()
    {
        var contexto = new Contexto();
        var solicitud = new SolicitudCertificacionTgss(Guid.NewGuid(), Guid.NewGuid(), Hoy, Guid.NewGuid());
        contexto.Repositorio.Agregar(solicitud);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false);
        var handler = contexto.CrearHandler(alcance);

        var resultado = await handler.Handle(
            new RegistrarRespuestaCertificacionTgssCommand(solicitud.Id, ResultadoCertificacionTgss.SinDescubiertos, Hoy),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("CertificacionTgss.NoEncontrada");
    }

    [Fact]
    public async Task Registra_la_respuesta_y_la_evidencia()
    {
        var contexto = new Contexto();
        var solicitud = new SolicitudCertificacionTgss(Guid.NewGuid(), Guid.NewGuid(), Hoy, Guid.NewGuid());
        contexto.Repositorio.Agregar(solicitud);
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new RegistrarRespuestaCertificacionTgssCommand(
                solicitud.Id, ResultadoCertificacionTgss.SinDescubiertos, Hoy, [1, 2, 3], "certificado.pdf"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        solicitud.Resultado.Should().Be(ResultadoCertificacionTgss.SinDescubiertos);
        solicitud.RespuestaRegistradaPorUsuarioId.Should().Be(UsuarioId);
        solicitud.EvidenciaNombreArchivo.Should().Be("certificado.pdf");
        contexto.UnitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Falla_cuando_la_solicitud_ya_tenia_respuesta()
    {
        var contexto = new Contexto();
        var solicitud = new SolicitudCertificacionTgss(Guid.NewGuid(), Guid.NewGuid(), Hoy, Guid.NewGuid());
        solicitud.RegistrarRespuesta(ResultadoCertificacionTgss.SinDescubiertos, Hoy, Guid.NewGuid());
        contexto.Repositorio.Agregar(solicitud);
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new RegistrarRespuestaCertificacionTgssCommand(solicitud.Id, ResultadoCertificacionTgss.ConDescubiertos, Hoy),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("CertificacionTgss.YaRespondida");
    }
}
