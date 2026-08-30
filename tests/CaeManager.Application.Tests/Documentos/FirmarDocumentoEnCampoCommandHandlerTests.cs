using System.Text;
using CaeManager.Application.Documentos.Commands.FirmarDocumentoEnCampo;
using CaeManager.Application.Documentos.Commands.GuardarFirmaGuardadaUsuario;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

public class FirmarDocumentoEnCampoCommandHandlerTests
{
    private const string TrazoPngBase64 = "aVZCT1J3MEtHZ289";
    private static readonly Guid UsuarioActualId = Guid.NewGuid();

    private sealed class Contexto
    {
        public DocumentoRepositorioFalso Documentos { get; } = new();
        public TiposDocumentoQueryContextFalso TiposDocumento { get; } = new();
        public FirmaDigitalDocumentoRepositorioFalso FirmasDigitales { get; } = new();
        public FirmaEnCampoDocumentoRepositorioFalso FirmasEnCampo { get; } = new();
        public FirmaGuardadaUsuarioRepositorioFalso FirmaGuardada { get; } = new();
        public SelloEmpresaRepositorioFalso SelloEmpresa { get; } = new();
        public FileStorageServiceFalso Almacenamiento { get; } = new();
        public EstampadoFirmaEnCampoPdfServiceFalso Estampador { get; } = new();
        public MediatorFalso Mediator { get; } = new() { Respuesta = Result.Exito() };
        public PublisherFalso Publisher { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();

        public FirmarDocumentoEnCampoCommandHandler CrearHandler(
            bool sinUsuario = false, string? rol = "GestorCae", AlcanceDatosServiceFalso? alcanceDatos = null) =>
            new(Documentos, TiposDocumento, alcanceDatos ?? new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
                FirmasDigitales, FirmasEnCampo, FirmaGuardada, SelloEmpresa, Almacenamiento, Estampador,
                new CurrentUserServiceFalso(sinUsuario ? null : UsuarioActualId, rol), new DirectorioUsuariosServiceFalso(),
                Mediator, Publisher, UnitOfWork, NullLogger<FirmarDocumentoEnCampoCommandHandler>.Instance);

        public async Task<Documento> CrearDocumentoConArchivoAsync(CaeManager.Domain.Documentos.TipoDocumento tipoDocumento)
        {
            TiposDocumento.ListaTiposDocumento.Add(tipoDocumento);
            var url = await Almacenamiento.GuardarAsync(
                new MemoryStream(Encoding.UTF8.GetBytes("contenido-original")), "original.pdf");
            var documento = Documento.DeTrabajador(Guid.NewGuid(), tipoDocumento.Id, new DateOnly(2026, 1, 1), null, url);
            Documentos.Agregar(documento);
            return documento;
        }
    }

    private static CaeManager.Domain.Documentos.TipoDocumento CrearTipoDocumento(
        PerfilDocumentoOficial perfil = PerfilDocumentoOficial.Ninguno)
    {
        var tipo = new CaeManager.Domain.Documentos.TipoDocumento(
            "Certificado", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Trabajador,
            CaeManager.Domain.Documentos.RequisitoDocumental.Si,
            CaeManager.Domain.Documentos.NaturalezaJuridica.PracticaSector);
        tipo.EstablecerPerfilDocumentoOficial(perfil);
        return tipo;
    }

    [Fact]
    public async Task Falla_cuando_el_documento_no_existe()
    {
        var contexto = new Contexto();
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new FirmarDocumentoEnCampoCommand(Guid.NewGuid(), TrazoPngBase64, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.NoEncontrado");
    }

    [Fact]
    public async Task Falla_cuando_el_documento_no_tiene_archivo()
    {
        var contexto = new Contexto();
        var tipo = CrearTipoDocumento();
        contexto.TiposDocumento.ListaTiposDocumento.Add(tipo);
        var documento = Documento.DeTrabajador(Guid.NewGuid(), tipo.Id, new DateOnly(2026, 1, 1), null);
        contexto.Documentos.Agregar(documento);
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new FirmarDocumentoEnCampoCommand(documento.Id, TrazoPngBase64, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.SinArchivo");
    }

    [Fact]
    public async Task Falla_cuando_el_tipo_de_documento_tiene_perfil_oficial()
    {
        var contexto = new Contexto();
        var tipo = CrearTipoDocumento(PerfilDocumentoOficial.CorrienteTgss);
        var documento = await contexto.CrearDocumentoConArchivoAsync(tipo);
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new FirmarDocumentoEnCampoCommand(documento.Id, TrazoPngBase64, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.FirmaEnCampoBloqueadaPorPerfilOficial");
        contexto.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Falla_cuando_el_archivo_ya_tiene_una_firma_digital_valida()
    {
        var contexto = new Contexto();
        var tipo = CrearTipoDocumento();
        var documento = await contexto.CrearDocumentoConArchivoAsync(tipo);
        contexto.FirmasDigitales.Agregar(new FirmaDigitalDocumento(
            documento.Id, 0, EstadoFirmaPdf.Valida, cadenaConfiable: true, ComprobacionRevocacion.ComprobadaEnLinea,
            "Emisor de prueba", "1234", esSelloDeOrgano: true, null, null, DateTime.UtcNow, true, true, null));
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new FirmarDocumentoEnCampoCommand(documento.Id, TrazoPngBase64, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.FirmaEnCampoBloqueadaPorFirmaExistente");
    }

    [Fact]
    public async Task Falla_con_un_trazo_que_no_es_base64_valido()
    {
        var contexto = new Contexto();
        var tipo = CrearTipoDocumento();
        var documento = await contexto.CrearDocumentoConArchivoAsync(tipo);
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new FirmarDocumentoEnCampoCommand(documento.Id, "no-es-base64!!", null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("FirmaEnCampo.TrazoInvalido");
    }

    [Fact]
    public async Task Caso_feliz_reemplaza_el_archivo_registra_la_firma_y_publica_el_evento()
    {
        var contexto = new Contexto();
        var tipo = CrearTipoDocumento();
        var documento = await contexto.CrearDocumentoConArchivoAsync(tipo);
        var urlAnterior = documento.ArchivoUrl!;
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new FirmarDocumentoEnCampoCommand(documento.Id, TrazoPngBase64, "Madrid"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        documento.ArchivoUrl.Should().NotBe(urlAnterior);
        contexto.FirmasEnCampo.Firmas.Should().ContainSingle();
        var firma = contexto.FirmasEnCampo.Firmas[0];
        firma.DocumentoId.Should().Be(documento.Id);
        firma.FirmanteUsuarioId.Should().Be(UsuarioActualId);
        firma.Ubicacion.Should().Be("Madrid");
        firma.HashSha256Pdf.Should().HaveLength(64);
        contexto.UnitOfWork.VecesGuardado.Should().Be(1);
        contexto.Publisher.Publicados.OfType<CaeManager.Application.Documentos.Eventos.DocumentacionCambiadaEvent>()
            .Should().ContainSingle(evento => evento.DocumentoId == documento.Id);
    }

    [Fact]
    public async Task Falla_cuando_no_se_puede_identificar_al_usuario_actual()
    {
        var contexto = new Contexto();
        var tipo = CrearTipoDocumento();
        var documento = await contexto.CrearDocumentoConArchivoAsync(tipo);
        var handler = contexto.CrearHandler(sinUsuario: true);

        var resultado = await handler.Handle(
            new FirmarDocumentoEnCampoCommand(documento.Id, TrazoPngBase64, null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("FirmaEnCampo.SinUsuario");
    }

    [Fact]
    public async Task Falla_cuando_UsarFirmaGuardada_y_el_usuario_no_tiene_firma_guardada()
    {
        var contexto = new Contexto();
        var tipo = CrearTipoDocumento();
        var documento = await contexto.CrearDocumentoConArchivoAsync(tipo);
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new FirmarDocumentoEnCampoCommand(documento.Id, null, null, UsarFirmaGuardada: true), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("FirmaEnCampo.SinFirmaGuardada");
    }

    [Fact]
    public async Task UsarFirmaGuardada_estampa_con_la_imagen_guardada_en_vez_del_trazo()
    {
        var contexto = new Contexto();
        var tipo = CrearTipoDocumento();
        var documento = await contexto.CrearDocumentoConArchivoAsync(tipo);
        var urlFirmaGuardada = await contexto.Almacenamiento.GuardarAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("imagen-firma-guardada")), "firma.png");
        contexto.FirmaGuardada.Agregar(new FirmaGuardadaUsuario(UsuarioActualId, urlFirmaGuardada, DateTime.UtcNow));
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new FirmarDocumentoEnCampoCommand(documento.Id, null, null, UsarFirmaGuardada: true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        Encoding.UTF8.GetString(contexto.Estampador.UltimoFirmaPng!).Should().Be("imagen-firma-guardada");
    }

    [Fact]
    public async Task SelloEmpresaId_informado_pasa_el_sello_al_estampador()
    {
        var contexto = new Contexto();
        var tipo = CrearTipoDocumento();
        var documento = await contexto.CrearDocumentoConArchivoAsync(tipo);
        var empresaId = Guid.NewGuid();
        var urlSello = await contexto.Almacenamiento.GuardarAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("imagen-sello")), "sello.png");
        contexto.SelloEmpresa.Agregar(new SelloEmpresa(empresaId, urlSello, DateTime.UtcNow));
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new FirmarDocumentoEnCampoCommand(documento.Id, TrazoPngBase64, null, SelloEmpresaId: empresaId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        Encoding.UTF8.GetString(contexto.Estampador.UltimoSelloPng!).Should().Be("imagen-sello");
    }

    [Fact]
    public async Task SelloEmpresaId_informado_sin_sello_guardado_firma_igualmente_sin_sello()
    {
        var contexto = new Contexto();
        var tipo = CrearTipoDocumento();
        var documento = await contexto.CrearDocumentoConArchivoAsync(tipo);
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new FirmarDocumentoEnCampoCommand(documento.Id, TrazoPngBase64, null, SelloEmpresaId: Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        contexto.Estampador.UltimoSelloPng.Should().BeNull();
    }

    /// <summary>
    /// Cierre de IDOR (auditoría de seguridad del módulo, 2026-08-30): antes
    /// de este fix, SelloEmpresaId no se comprobaba contra la cartera del
    /// usuario — un operador podía estampar el sello de cualquier Empresa
    /// del tenant sobre un documento visible.
    /// </summary>
    [Fact]
    public async Task SelloEmpresaId_fuera_de_la_cartera_del_usuario_falla_explicitamente()
    {
        var contexto = new Contexto();
        var tipo = CrearTipoDocumento();
        var documento = await contexto.CrearDocumentoConArchivoAsync(tipo);
        var empresaId = Guid.NewGuid();
        var urlSello = await contexto.Almacenamiento.GuardarAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("imagen-sello")), "sello.png");
        contexto.SelloEmpresa.Agregar(new SelloEmpresa(empresaId, urlSello, DateTime.UtcNow));
        var handler = contexto.CrearHandler(alcanceDatos: new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: []));

        var resultado = await handler.Handle(
            new FirmarDocumentoEnCampoCommand(documento.Id, TrazoPngBase64, null, SelloEmpresaId: empresaId), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("FirmaEnCampo.SelloNoAutorizado");
        contexto.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task GuardarComoFirmaHabitual_envia_el_command_de_guardar_firma_guardada()
    {
        var contexto = new Contexto();
        var tipo = CrearTipoDocumento();
        var documento = await contexto.CrearDocumentoConArchivoAsync(tipo);
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new FirmarDocumentoEnCampoCommand(documento.Id, TrazoPngBase64, null, GuardarComoFirmaHabitual: true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        contexto.Mediator.Enviados.OfType<GuardarFirmaGuardadaUsuarioCommand>().Should().ContainSingle(
            c => c.EsImagenDibujada);
    }

    [Fact]
    public async Task GuardarComoFirmaHabitual_no_reenvia_nada_cuando_se_reutiliza_la_firma_guardada()
    {
        var contexto = new Contexto();
        var tipo = CrearTipoDocumento();
        var documento = await contexto.CrearDocumentoConArchivoAsync(tipo);
        var urlFirmaGuardada = await contexto.Almacenamiento.GuardarAsync(
            new MemoryStream(Encoding.UTF8.GetBytes("imagen-firma-guardada")), "firma.png");
        contexto.FirmaGuardada.Agregar(new FirmaGuardadaUsuario(UsuarioActualId, urlFirmaGuardada, DateTime.UtcNow));
        var handler = contexto.CrearHandler();

        var resultado = await handler.Handle(
            new FirmarDocumentoEnCampoCommand(documento.Id, null, null, UsarFirmaGuardada: true, GuardarComoFirmaHabitual: true),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        contexto.Mediator.Enviados.OfType<GuardarFirmaGuardadaUsuarioCommand>().Should().BeEmpty();
    }
}
