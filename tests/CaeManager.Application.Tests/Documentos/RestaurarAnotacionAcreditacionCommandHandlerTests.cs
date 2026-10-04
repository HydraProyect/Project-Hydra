using CaeManager.Application.Documentos.Commands.ConfirmarVigenciaAcreditacion;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionAceptada;
using CaeManager.Application.Documentos.Commands.RestaurarAnotacionAcreditacion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// «Deshacer» tras anotar la vigencia (ficha 09). El recibo que devuelve la anotación
/// (valor previo + versión) es lo único que alimenta la restauración: estos tests
/// recorren la cadena completa anotación → recibo → restauración.
/// </summary>
public class RestaurarAnotacionAcreditacionCommandHandlerTests
{
    private static readonly VigenciaEnPlataforma Nueva = VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 3, 14));

    private sealed class Mundo
    {
        public Documento Documento { get; } = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 1, 1), VigenciaDocumento.NoCaduca);
        public AcreditacionDocumentoPlataforma Acreditacion { get; }
        public UnitOfWorkFalso UnitOfWork { get; } = new();
        private readonly DocumentoRepositorioFalso _documentos = new();
        private readonly AcreditacionDocumentoPlataformaRepositorioFalso _acreditaciones = new();
        private readonly AlcanceDatosServiceFalso _alcance;

        public Mundo(bool dentroDeAlcance = true)
        {
            Acreditacion = new AcreditacionDocumentoPlataforma(Documento.Id, Guid.NewGuid());
            _documentos.Agregar(Documento);
            _acreditaciones.Agregar(Acreditacion);
            // Sin alcance total y sin el Trabajador del Documento entre los visibles, el Documento queda fuera.
            _alcance = dentroDeAlcance
                ? new AlcanceDatosServiceFalso()
                : new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [Guid.NewGuid()]);
        }

        public MarcarAcreditacionAceptadaCommandHandler Aceptar() =>
            new(_acreditaciones, _documentos, _alcance, new ProyectosQueryContextFalso(), UnitOfWork);

        public ConfirmarVigenciaAcreditacionCommandHandler SoloVigencia() =>
            new(_acreditaciones, _documentos, _alcance, new ProyectosQueryContextFalso(), UnitOfWork);

        public RestaurarAnotacionAcreditacionCommandHandler Restaurar() =>
            new(_acreditaciones, _documentos, _alcance, new ProyectosQueryContextFalso(), UnitOfWork);
    }

    [Fact]
    public async Task Marcar_aceptado_devuelve_el_valor_previo_y_la_version_resultante()
    {
        var m = new Mundo();
        m.Acreditacion.MarcarSubida();

        var r = await m.Aceptar().Handle(new MarcarAcreditacionAceptadaCommand(m.Acreditacion.Id, Nueva), CancellationToken.None);

        r.EsExitoso.Should().BeTrue();
        r.Valor.EstadoPrevio.Should().Be(EstadoAcreditacion.Subida);
        r.Valor.VigenciaPrevia.Should().Be(VigenciaEnPlataforma.SinConfirmar);
        r.Valor.VersionResultante.Should().Be(m.Acreditacion.Version);
    }

    [Fact]
    public async Task Deshacer_devuelve_el_estado_y_la_vigencia_exactos_incluido_sin_confirmar()
    {
        var m = new Mundo();
        m.Acreditacion.MarcarSubida();
        var recibo = (await m.Aceptar().Handle(new MarcarAcreditacionAceptadaCommand(m.Acreditacion.Id, Nueva), CancellationToken.None)).Valor;

        var r = await m.Restaurar().Handle(
            new RestaurarAnotacionAcreditacionCommand(recibo.AcreditacionId, recibo.EstadoPrevio, recibo.VigenciaPrevia, recibo.VersionResultante),
            CancellationToken.None);

        r.EsExitoso.Should().BeTrue();
        m.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Subida);
        m.Acreditacion.Vigencia.Should().Be(VigenciaEnPlataforma.SinConfirmar, "Sin confirmar no es lo mismo que anotada");
    }

    [Fact]
    public async Task Deshacer_una_vigencia_corregida_devuelve_la_que_el_gestor_habia_confirmado_a_mano()
    {
        var m = new Mundo();
        m.Acreditacion.MarcarAceptada(VigenciaEnPlataforma.NoVenceAqui);
        var recibo = (await m.SoloVigencia().Handle(new ConfirmarVigenciaAcreditacionCommand(m.Acreditacion.Id, Nueva), CancellationToken.None)).Valor;

        var r = await m.Restaurar().Handle(
            new RestaurarAnotacionAcreditacionCommand(recibo.AcreditacionId, recibo.EstadoPrevio, recibo.VigenciaPrevia, recibo.VersionResultante),
            CancellationToken.None);

        r.EsExitoso.Should().BeTrue();
        m.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        m.Acreditacion.Vigencia.Should().Be(VigenciaEnPlataforma.NoVenceAqui);
    }

    [Fact]
    public async Task Rechaza_si_la_acreditacion_cambio_desde_la_anotacion_y_no_escribe()
    {
        var m = new Mundo();
        m.Acreditacion.MarcarSubida();
        var recibo = (await m.Aceptar().Handle(new MarcarAcreditacionAceptadaCommand(m.Acreditacion.Id, Nueva), CancellationToken.None)).Valor;
        var guardadosAntes = m.UnitOfWork.VecesGuardado;

        var r = await m.Restaurar().Handle(
            new RestaurarAnotacionAcreditacionCommand(recibo.AcreditacionId, recibo.EstadoPrevio, recibo.VigenciaPrevia, Guid.NewGuid()),
            CancellationToken.None);

        r.EsFallido.Should().BeTrue();
        r.Error.Codigo.Should().Be("Concurrencia.Conflicto");
        r.Error.Mensaje.Should().Contain("a mano", "el mensaje dice qué hacer");
        m.Acreditacion.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        m.Acreditacion.Vigencia.Should().Be(Nueva);
        m.UnitOfWork.VecesGuardado.Should().Be(guardadosAntes);
    }

    [Fact]
    public async Task Es_idempotente_si_ya_esta_en_los_valores_previos_no_vuelve_a_escribir()
    {
        var m = new Mundo();
        var comando = new RestaurarAnotacionAcreditacionCommand(
            m.Acreditacion.Id, EstadoAcreditacion.PendienteDeSubir, VigenciaEnPlataforma.SinConfirmar, Guid.NewGuid());

        var r = await m.Restaurar().Handle(comando, CancellationToken.None);

        r.EsExitoso.Should().BeTrue();
        m.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Fuera_de_alcance_responde_igual_que_una_acreditacion_inexistente()
    {
        var m = new Mundo(dentroDeAlcance: false);

        var r = await m.Restaurar().Handle(
            new RestaurarAnotacionAcreditacionCommand(m.Acreditacion.Id, EstadoAcreditacion.Subida, VigenciaEnPlataforma.SinConfirmar, Guid.NewGuid()),
            CancellationToken.None);

        r.EsFallido.Should().BeTrue();
        r.Error.Codigo.Should().Be("Acreditacion.NoEncontrada");
        m.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void La_version_es_obligatoria_en_el_comando()
    {
        var v = new RestaurarAnotacionAcreditacionCommandValidator().Validate(
            new RestaurarAnotacionAcreditacionCommand(Guid.NewGuid(), EstadoAcreditacion.Subida, VigenciaEnPlataforma.SinConfirmar, Guid.Empty));

        v.IsValid.Should().BeFalse("Guid.Empty significa «sin comprobación» en ConcurrenciaOptimista y aquí no puede valer");
    }
}
