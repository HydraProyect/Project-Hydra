using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// D-4 sobre la #2005 (decisión del 2026-11-15): el sello del expediente de una Visita usa la misma regla de bloqueo que el
/// resto de superficies (<c>ReglaBloqueoDeAcceso</c> a través de <c>IEvaluacionDeAccesoDelTenant</c>), con
/// <c>BloqueaAcceso</c> y la tolerancia por Centro. Cada caso lleva su control: el mismo escenario con un detalle distinto
/// da el resultado contrario, para que el verde no se deba a otra cosa. Contextos falsos, sin Postgres.
/// </summary>
public class ExpedienteVisitaConLaReglaDeBloqueoComunTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    private readonly CentrosQueryContextFalso _centros = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly VisitasQueryContextFalso _visitas = new();

    private readonly Empresa _contratista = new("Contratista Demo SL", "B12345674");
    private readonly Centro _centro;
    private readonly Trabajador _ana;

    public ExpedienteVisitaConLaReglaDeBloqueoComunTests()
    {
        var titular = Empresa.CrearComoCliente("Titular Demo SA", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        _centro = new Centro(titular.Id, _contratista.Id, "Nave Norte");
        _ana = Trabajador.DeEmpresa(_contratista.Id, "Ana", "García", "12345678Z");
        _centros.ListaCentros.Add(_centro);
        _trabajadores.ListaTrabajadores.Add(_ana);
        _configuracion.ListaParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 7));
    }

    private TipoDocumento Tipo(AmbitoAplicacion ambito, RequisitoDocumental requerido = RequisitoDocumental.Si)
    {
        var tipo = new TipoDocumento("Tipo " + ambito, vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1, ambito, requerido);
        _tipos.ListaTiposDocumento.Add(tipo);
        return tipo;
    }

    private void Bloqueante(TipoDocumento tipo, int? toleranciaDias = null) =>
        _tipos.ListaTiposDocumentoCentros.Add(
            new TipoDocumentoCentro(tipo.Id, _centro.Id, incluido: true, bloqueaAcceso: true, toleranciaDias: toleranciaDias));

    private async Task<bool> SellaConAna()
    {
        var visita = new Visita(_centro.Id, Hoy.AddDays(3), Hoy.AddDays(3), null);
        visita.RegistrarOrigenSolicitud(Guid.NewGuid(), DateTime.UtcNow.AddHours(-1));
        _visitas.ListaVisitas.Add(visita);
        _visitas.ListaVisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, _ana.Id));
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Visitas.Add(visita);

        var sellado = await EvaluadorExpedienteDePrueba.Crear(
                _visitas, _centros, _documentos, _tipos, _trabajadores, _configuracion, repositorio)
            .EvaluarAsync(visita.Id);

        (visita.FechaHoraExpedienteCompletoUtc is not null).Should().Be(sellado, "devolver true es haber sellado");
        return sellado;
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(0, false)]
    public async Task Un_documento_vencido_dentro_de_la_tolerancia_del_centro_no_impide_el_sello(int toleranciaDias, bool selloEsperado)
    {
        var tipo = Tipo(AmbitoAplicacion.Trabajador);
        Bloqueante(tipo, toleranciaDias);
        _documentos.ListaDocumentos.Add(Documento.DeTrabajador(
            _ana.Id, tipo.Id, Hoy.AddYears(-1), VigenciaDocumento.VenceEl(Hoy.AddDays(-3))));

        (await SellaConAna()).Should().Be(selloEsperado, toleranciaDias > 0
            ? "vencido hace 3 días con 10 de tolerancia: el Centro le deja entrar, como en el resto de superficies"
            : "control: sin tolerancia el mismo documento vencido bloquea y el expediente no está completo");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Solo_espera_los_tipos_que_el_centro_marca_como_bloqueantes(bool marcadoBloqueante, bool selloEsperado)
    {
        var tipo = Tipo(AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        if (marcadoBloqueante)
            Bloqueante(tipo);
        else
            _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(tipo.Id, _centro.Id, incluido: true, bloqueaAcceso: false));

        (await SellaConAna()).Should().Be(selloEsperado, marcadoBloqueante
            ? "control: el mismo tipo ausente, marcado BloqueaAcceso, bloquea a Ana"
            : "un tipo requerido que el Centro no marca como bloqueante no bloquea el acceso ni el sello");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Un_documento_bloqueante_de_la_empresa_del_trabajador_ausente_impide_el_sello(bool conDocumento, bool selloEsperado)
    {
        var tipo = Tipo(AmbitoAplicacion.Empresa);
        Bloqueante(tipo);
        if (conDocumento)
            _documentos.ListaDocumentos.Add(Documento.DeEmpresa(
                _contratista.Id, tipo.Id, Hoy.AddMonths(-1), VigenciaDocumento.VenceEl(Hoy.AddYears(1))));

        (await SellaConAna()).Should().Be(selloEsperado, conDocumento
            ? "control: con el documento de su Empresa vigente no falta nada"
            : "un documento de Empresa bloqueante ausente bloquea a todos sus Trabajadores en el Centro");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Un_participante_que_ya_no_se_resuelve_impide_el_sello(bool conParticipanteEliminado, bool selloEsperado)
    {
        // Sin requisitos bloqueantes: Ana sola sella. Un participante eliminado (no lo resuelve el contexto de
        // Trabajadores, como con el filtro de borrado lógico) no se puede evaluar y el sello es irreversible.
        var visita = new Visita(_centro.Id, Hoy.AddDays(3), Hoy.AddDays(3), null);
        visita.RegistrarOrigenSolicitud(Guid.NewGuid(), DateTime.UtcNow.AddHours(-1));
        _visitas.ListaVisitas.Add(visita);
        _visitas.ListaVisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, _ana.Id));
        if (conParticipanteEliminado)
            _visitas.ListaVisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, Guid.NewGuid()));
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Visitas.Add(visita);

        var sellado = await EvaluadorExpedienteDePrueba.Crear(
                _visitas, _centros, _documentos, _tipos, _trabajadores, _configuracion, repositorio)
            .EvaluarAsync(visita.Id);

        sellado.Should().Be(selloEsperado, conParticipanteEliminado
            ? "un participante que no se puede evaluar no da el expediente por completo"
            : "control: con todos los participantes resueltos y nada bloqueante, sella");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task El_documento_de_una_subcontrata_reevalua_las_visitas_de_sus_trabajadores_en_el_centro_de_otra_empresa(
        bool documentoDeLaSubcontrata, bool selloEsperado)
    {
        // El Centro es de la contratista; Bea es de una Subcontrata. La regla común exige el documento de Empresa de la
        // Subcontrata, así que al subirlo hay que reevaluar la Visita aunque el Centro sea de otra Empresa.
        var subcontrata = Guid.NewGuid();
        var bea = Trabajador.DeSubcontrata(subcontrata, "Bea", "López", "87654321X");
        _trabajadores.ListaTrabajadores.Add(bea);
        var tipo = Tipo(AmbitoAplicacion.Empresa);
        Bloqueante(tipo);

        var visita = new Visita(_centro.Id, Hoy.AddDays(3), Hoy.AddDays(3), null);
        visita.RegistrarOrigenSolicitud(Guid.NewGuid(), DateTime.UtcNow.AddHours(-1));
        _visitas.ListaVisitas.Add(visita);
        _visitas.ListaVisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, bea.Id));
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Visitas.Add(visita);
        var evaluador = EvaluadorExpedienteDePrueba.Crear(
            _visitas, _centros, _documentos, _tipos, _trabajadores, _configuracion, repositorio);

        (await evaluador.EvaluarAsync(visita.Id)).Should().BeFalse("sin el documento de su Subcontrata, Bea está bloqueada");

        var documento = Documento.DeEmpresa(
            documentoDeLaSubcontrata ? subcontrata : _contratista.Id, tipo.Id, Hoy.AddMonths(-1), VigenciaDocumento.VenceEl(Hoy.AddYears(1)));
        _documentos.ListaDocumentos.Add(documento);

        await evaluador.EvaluarPorDocumentoAsync(documento.Id);

        (visita.FechaHoraExpedienteCompletoUtc is not null).Should().Be(selloEsperado, documentoDeLaSubcontrata
            ? "el documento de la Empresa de Bea dispara la reevaluación de su Visita y completa el expediente"
            : "control: el documento de la Empresa del Centro no es el que le falta a Bea");
    }

    private sealed class VisitasQueryContextFalso : IVisitasQueryContext
    {
        public List<Visita> ListaVisitas { get; } = [];
        public List<VisitaTrabajador> ListaVisitasTrabajadores { get; } = [];

        public IQueryable<Visita> Visitas => new TestAsyncQueryable<Visita>(ListaVisitas.AsQueryable());
        public IQueryable<VisitaTrabajador> VisitasTrabajadores => new TestAsyncQueryable<VisitaTrabajador>(ListaVisitasTrabajadores.AsQueryable());
    }
}
