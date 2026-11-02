using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.AceptarDeteccionesIaEnBloque;
using CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento;
using CaeManager.Application.Documentos.Verificacion;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.DocumentosIa;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Una revisión con alerta determinista o lectura parcial no se acepta en bloque, porque en bloque nadie ve su motivo: lo
/// impide el servidor, con el handler real de la aceptación individual detrás del de bloque. De una en una sigue
/// permitida. La misma propiedad contra PostgreSQL está en <c>AceptarDeteccionesIaEnBloqueTests</c> (integración).
/// </summary>
public class AlertasEnRevisionIaTests
{
    private static readonly DateOnly FechaLeida = new(2026, 2, 10);

    public static TheoryData<string> MotivosConAlerta() =>
    [
        MotivosRevisionIa.SinContraste,
        MotivosRevisionIa.TextoDirigidoAlLector,
        MotivosRevisionIa.NoAptoORestriccion,
        MotivosRevisionIa.FechaEmisionNoEscrita,
        MotivosRevisionIa.FirmaNoCuadraConElContenido,
        MotivosRevisionIa.CertificadoEnNegativo,
        MotivosRevisionIa.CifNoCoincideConElPropietario("B99999999", "B12345674"),
        $"{MotivosRevisionIa.CertificadoEnNegativo} La fecha de emisión introducida no coincide con la del documento.",
        MotivosRevisionIa.LecturaParcial(26),
        $"{MotivosRevisionIa.LecturaParcial(26)}; Confianza baja (62%)",
    ];

    private sealed class Escenario
    {
        private readonly TiposDocumentoQueryContextFalso _tipos = new();
        private readonly TipoDocumento _tipo = new("Formación", 12, true, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);

        public DocumentoRepositorioFalso Documentos { get; } = new();
        public RevisionesFalsas Revisiones { get; } = new();
        public AprobacionesFalsas Aprobaciones { get; } = new();
        public UnitOfWorkFalso UnitOfWork { get; } = new();

        public Escenario() => _tipos.ListaTiposDocumento.Add(_tipo);

        public (Documento Documento, RevisionIaDocumento Revision) Sembrar(string motivo)
        {
            var documento = Documento.DeTrabajador(Guid.NewGuid(), _tipo.Id, new DateOnly(2025, 1, 1), VigenciaDocumento.NoCaduca);
            Documentos.Agregar(documento);
            var revision = RevisionIaDocumento.Crear(documento.Id, 99, "Formación", FechaLeida, null, true, motivo);
            Revisiones.Agregar(revision);
            return (documento, revision);
        }

        public AplicarDeteccionIaDocumentoCommandHandler Individual(IAlcanceDatosService? alcance = null) => new(
            Revisiones, Documentos, Aprobaciones, new AuditoriaExtraccionIaRepositorioFalso(), _tipos,
            alcance ?? new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(), new CurrentUserServiceFalso(Guid.NewGuid()),
            new PublisherFalso(), UnitOfWork);

        public AceptarDeteccionesIaEnBloqueCommandHandler Bloque() => new(
            new MediadorDirecto(Individual()), new DescarteFalso(), NullLogger<AceptarDeteccionesIaEnBloqueCommandHandler>.Instance);
    }

    [Theory]
    [MemberData(nameof(MotivosConAlerta))]
    public async Task En_bloque_la_revision_con_alerta_falla_con_su_mensaje_sin_tocar_nada_ni_tumbar_las_demas(string motivo)
    {
        var escenario = new Escenario();
        var conAlerta = escenario.Sembrar(motivo);
        var limpia = escenario.Sembrar("Confianza baja (62%)");

        var resultado = await escenario.Bloque().Handle(
            new AceptarDeteccionesIaEnBloqueCommand([conAlerta.Revision.Id, limpia.Revision.Id]), CancellationToken.None);

        resultado.Valor.Aceptadas.Should().Be(1, "control positivo: la limpia se acepta, y va detrás de la que falla");
        var fallida = resultado.Valor.Resultados.Single(r => r.RevisionId == conAlerta.Revision.Id);
        fallida.Aceptada.Should().BeFalse();
        fallida.CodigoError.Should().Be(CodigosRevisionIa.CodigoAlertaARevisarIndividualmente);
        fallida.Mensaje.Should().Contain("alerta").And.Contain("de una en una");

        conAlerta.Revision.Resuelta.Should().BeFalse();
        conAlerta.Documento.FechaEmision.Should().Be(new DateOnly(2025, 1, 1));
        escenario.Aprobaciones.Aprobaciones.Should().ContainSingle("solo aprueba la limpia")
            .Which.DocumentoId.Should().Be(limpia.Documento.Id);
        limpia.Revision.Resuelta.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(MotivosConAlerta))]
    public async Task De_una_en_una_la_revision_con_alerta_se_puede_aceptar(string motivo)
    {
        var escenario = new Escenario();
        var (documento, revision) = escenario.Sembrar(motivo);

        var resultado = await escenario.Individual().Handle(new AplicarDeteccionIaDocumentoCommand(revision.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        revision.Resuelta.Should().BeTrue();
        documento.FechaEmision.Should().Be(FechaLeida);
        escenario.Aprobaciones.Aprobaciones.Should().ContainSingle();
    }

    [Fact]
    public async Task Fuera_de_alcance_no_revela_que_la_revision_tiene_alerta()
    {
        var escenario = new Escenario();
        var (_, revision) = escenario.Sembrar(MotivosRevisionIa.NoAptoORestriccion);

        var resultado = await escenario.Individual(new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [])).Handle(
            new AplicarDeteccionIaDocumentoCommand(revision.Id, SoloSinAlertaDeterminista: true), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("RevisionIa.NoEncontrada");
    }

    [Fact]
    public void La_lectura_parcial_se_persiste_como_motivo_con_las_paginas_sin_leer_y_cuenta_como_alerta()
    {
        var motivos = VerificacionIaDocumentoService.ComputarMotivos(
            DocumentoLeido(), TipoEsperado(), Lectura(new ContrasteTextoDocumentoDto(false, false, [FechaLeida])) with { PaginasSinLeer = 26 });

        motivos[0].Should().Be(MotivosRevisionIa.LecturaParcial(26)).And.Contain("26 página(s) sin leer");
        RevisionPara(motivos).TieneAlertaDeterminista.Should().BeTrue();
    }

    [Fact]
    public void Una_lectura_completa_y_contrastada_no_tiene_alerta_aunque_tenga_otros_motivos()
    {
        var limpia = Lectura(new ContrasteTextoDocumentoDto(false, false, [FechaLeida]));

        var motivos = VerificacionIaDocumentoService.ComputarMotivos(DocumentoLeido(), TipoEsperado(), limpia with { ConfianzaGeneral = 50 });

        motivos.Should().ContainSingle("control positivo: la baja confianza sí es un motivo").Which.Should().Contain("Confianza baja");
        RevisionPara(motivos).TieneAlertaDeterminista.Should().BeFalse();
    }

    [Fact]
    public void Ninguna_alerta_se_pierde_al_recortar_el_motivo_cuando_coinciden_todos()
    {
        var peorCaso = new MetadatosDocumentoExtraidosDto(
            "Otro tipo", new DateOnly(2026, 3, 1), null, null, 1, null, new ContrasteTextoDocumentoDto(true, true, []), PaginasSinLeer: 99_999);

        var motivos = VerificacionIaDocumentoService.ComputarMotivos(DocumentoLeido(), TipoEsperado(), peorCaso);

        string.Join("; ", motivos).Length.Should().BeGreaterThan(RevisionIaDocumento.LongitudMaximaMotivo, "control positivo: este motivo se recorta");
        RevisionPara(motivos).Motivo.Should()
            .Contain(MotivosRevisionIa.LecturaParcial(99_999))
            .And.Contain(MotivosRevisionIa.TextoDirigidoAlLector)
            .And.Contain(MotivosRevisionIa.NoAptoORestriccion)
            .And.Contain(MotivosRevisionIa.FechaEmisionNoEscrita);
    }

    /// <summary>
    /// Las alertas caben enteras para que la persona las vea todas, no solo para que quede alguna. El peor caso se mide por
    /// quien escribe el motivo, porque la suma del catálogo entero no es alcanzable: cada revisión la crea una sola
    /// verificación (y nada reescribe su motivo), <see cref="MotivosRevisionIa.SinContraste"/> excluye las alertas del
    /// contraste, y en la validación oficial la firma alterada sale antes del cotejo.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void El_peor_caso_de_la_lectura_ia_cabe_entero_en_el_motivo(bool conContraste)
    {
        var contraste = conContraste ? new ContrasteTextoDocumentoDto(true, true, []) : null;
        var peorCaso = new MetadatosDocumentoExtraidosDto(
            "Formación", FechaLeida, null, true, 96, null, contraste, PaginasSinLeer: int.MaxValue);

        var alertas = VerificacionIaDocumentoService.ComputarMotivos(DocumentoLeido(), TipoEsperado(), peorCaso)
            .Where(MotivosRevisionIa.ContieneAlerta)
            .ToList();

        alertas.Should().HaveCount(conContraste ? 4 : 2, "control positivo: están todas las que pueden coincidir");
        string.Join("; ", alertas).Length.Should().BeLessThanOrEqualTo(RevisionIaDocumento.LongitudMaximaMotivo);
    }

    [Fact]
    public void El_peor_caso_de_la_validacion_oficial_cabe_entero_en_el_motivo()
    {
        var cifMaximo = new string('X', VerificacionDocumentoOficial.LongitudMaximaCif);

        var alertas = $"{MotivosRevisionIa.CertificadoEnNegativo} {MotivosRevisionIa.CifNoCoincideConElPropietario(cifMaximo, cifMaximo)}";

        alertas.Length.Should().BeLessThanOrEqualTo(RevisionIaDocumento.LongitudMaximaMotivo);
        MotivosRevisionIa.FirmaNoCuadraConElContenido.Length.Should().BeLessThanOrEqualTo(RevisionIaDocumento.LongitudMaximaMotivo);
    }

    private static Documento DocumentoLeido() =>
        Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), FechaLeida, VigenciaDocumento.NoCaduca);

    private static TipoDocumento TipoEsperado() =>
        new("Formación", 12, true, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);

    private static MetadatosDocumentoExtraidosDto Lectura(ContrasteTextoDocumentoDto contraste) =>
        new("Formación", FechaLeida, null, true, 96, null, contraste);

    private static RevisionIaDocumento RevisionPara(IReadOnlyList<string> motivos) =>
        RevisionIaDocumento.Crear(Guid.NewGuid(), 96, "Formación", FechaLeida, null, true, string.Join("; ", motivos));

    private sealed class RevisionesFalsas : IRevisionIaDocumentoRepository
    {
        private readonly List<RevisionIaDocumento> _revisiones = [];
        public Task<RevisionIaDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(_revisiones.SingleOrDefault(r => r.Id == id));
        public void Agregar(RevisionIaDocumento revision) => _revisiones.Add(revision);
    }

    private sealed class AprobacionesFalsas : IAprobacionDocumentoRepository
    {
        public List<AprobacionDocumento> Aprobaciones { get; } = [];
        public void Agregar(AprobacionDocumento aprobacion) => Aprobaciones.Add(aprobacion);
    }

    private sealed class DescarteFalso : IDescarteCambiosPendientes
    {
        public void DescartarCambiosPendientes()
        {
        }
    }

    /// <summary>Despacha al handler real: lo que se prueba es que el bloque activa la regla, no que un doble la imite.</summary>
    private sealed class MediadorDirecto(AplicarDeteccionIaDocumentoCommandHandler individual) : IMediator
    {
        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default) =>
            request is AplicarDeteccionIaDocumentoCommand c
                ? (T)(object)await individual.Handle(c, cancellationToken)
                : throw new NotSupportedException(request.GetType().Name);

        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => throw new NotSupportedException();
    }
}
