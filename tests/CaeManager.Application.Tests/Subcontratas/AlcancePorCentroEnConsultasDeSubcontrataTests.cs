using CaeManager.Application.Empresas.Queries.ObtenerCentrosConActividadDeEmpresa;
using CaeManager.Application.Subcontratas.Queries.ObtenerCentrosConActividadDeSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerEvidenciaVerificacionParaDescarga;
using CaeManager.Application.Subcontratas.Queries.ObtenerSupervisionSubcontrata;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Subcontratas;

/// <summary>
/// Hallazgo 2026-10-09 (Project-Hydra-Negocio/seguridad/HALLAZGO-ALCANCE-USUARIO-CLIENTE-EMPRESARIAL-SUBCONTRATAS-2026-10-09.md):
/// que una subcontrata sea visible no hace visibles todos los Centros donde trabaja. Quien solo
/// alcanza algunos Centros del Tenant propietario —el usuario de un Cliente empresarial, o un
/// Gestor CAE cuya Asignación de Cartera cuelga de una Asignación de Operación acotada a un
/// Cliente empresarial (la cartera en sí es siempre el Tenant entero: D-7)— recibía, desde el panel
/// de la subcontrata, el nombre de los Centros de OTROS Clientes empresariales y su razón social.
///
/// Las consultas se cruzan con los Centros visibles para quien pregunta
/// (<c>IAlcanceDatosService.ObtenerCentroIdsVisiblesAsync</c>). No cambia qué subcontratas son
/// visibles: en todos los casos la subcontrata lo es. Contextos falsos, sin Postgres: lo que se
/// prueba es la regla de Application, no RLS.
/// </summary>
public class AlcancePorCentroEnConsultasDeSubcontrataTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    private readonly CentrosQueryContextFalso _centros = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly SubcontratasQueryContextFalso _subcontratas = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly ConfiguracionQueryContextFalso _configuracion = new();

    private readonly Empresa _clienteEmpresarialPropio =
        Empresa.CrearComoCliente("Cliente empresarial propio SA", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
    private readonly Empresa _otroClienteEmpresarial =
        Empresa.CrearComoCliente("Otro Cliente empresarial SA", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
    private readonly Empresa _contratista = new("Contratista del Tenant SL", "B12345674");
    private readonly Empresa _subcontrata = Empresa.CrearComoSubcontrata("Subcontrata visible SL", "B12345674", "Estandar");

    private readonly Centro _centroPropio;
    private readonly Centro _centroAjeno;
    private readonly TipoDocumento _tipoExigido;
    private readonly VerificacionExternaSubcontrata _verificacionEnCentroPropio;
    private readonly VerificacionExternaSubcontrata _verificacionEnCentroAjeno;

    public AlcancePorCentroEnConsultasDeSubcontrataTests()
    {
        _centroPropio = new Centro(_clienteEmpresarialPropio.Id, _contratista.Id, "Planta propia");
        _centroAjeno = new Centro(_otroClienteEmpresarial.Id, _contratista.Id, "Planta ajena");
        _centros.ListaCentros.AddRange([_centroPropio, _centroAjeno]);
        _empresas.ListaEmpresas.AddRange([_clienteEmpresarialPropio, _otroClienteEmpresarial, _contratista, _subcontrata]);

        // La subcontrata presta servicio a los dos Clientes empresariales: sus Centros son
        // candidatos a primera verificación (CentrosSeleccionables).
        _empresas.ListaRelacionesEmpresariales.AddRange([
            RelacionEmpresarial.Crear(_subcontrata.Id, _clienteEmpresarialPropio.Id, DateTime.UtcNow.AddMonths(-6)),
            RelacionEmpresarial.Crear(_subcontrata.Id, _otroClienteEmpresarial.Id, DateTime.UtcNow.AddMonths(-6))]);

        // Dos Trabajadores de la subcontrata: uno en los dos Centros, otro solo en el ajeno.
        var enAmbos = Trabajador.DeSubcontrata(_subcontrata.Id, "Pepe", "Ruiz", "11223344B");
        var soloEnElAjeno = Trabajador.DeSubcontrata(_subcontrata.Id, "Lola", "Sanz", "77189989B");
        // Y uno de la contratista en los dos, para la consulta gemela de Empresa.
        var deLaContratista = Trabajador.DeEmpresa(_contratista.Id, "Ana", "Garcia", "12345678Z");
        _trabajadores.ListaTrabajadores.AddRange([enAmbos, soloEnElAjeno, deLaContratista]);
        _asignaciones.ListaAsignaciones.AddRange([
            new Asignacion(enAmbos.Id, _centroPropio.Id, Hoy.AddDays(-10)),
            new Asignacion(enAmbos.Id, _centroAjeno.Id, Hoy.AddDays(-10)),
            new Asignacion(soloEnElAjeno.Id, _centroAjeno.Id, Hoy.AddDays(-10)),
            new Asignacion(deLaContratista.Id, _centroPropio.Id, Hoy.AddDays(-10)),
            new Asignacion(deLaContratista.Id, _centroAjeno.Id, Hoy.AddDays(-10))]);

        _tipoExigido = new TipoDocumento(
            "Reconocimiento médico", vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1,
            AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        _tipos.ListaTiposDocumento.Add(_tipoExigido);
        _configuracion.ListaParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 7));

        _verificacionEnCentroPropio = VerificacionConEvidencia(_centroPropio, "propia.pdf");
        _verificacionEnCentroAjeno = VerificacionConEvidencia(_centroAjeno, "ajena.pdf");
        _subcontratas.ListaVerificacionesExternaSubcontrata.AddRange([_verificacionEnCentroPropio, _verificacionEnCentroAjeno]);
    }

    private VerificacionExternaSubcontrata VerificacionConEvidencia(Centro centro, string nombreArchivo)
    {
        var verificacion = new VerificacionExternaSubcontrata(
            _subcontrata.Id, centro.Id, _tipoExigido.Id, Hoy.AddDays(-3), ResultadoVerificacionExterna.Valido, Guid.NewGuid());
        verificacion.AdjuntarEvidencia($"evidencias/{nombreArchivo}", nombreArchivo);
        return verificacion;
    }

    /// <summary>La subcontrata y la contratista son visibles; de los dos Centros, solo el propio.</summary>
    private AlcanceDatosServiceFalso AlcanceSoloAlCentroPropio() => new(
        tieneAccesoTotal: false,
        subcontrataIdsVisibles: [_subcontrata.Id],
        empresaIdsVisibles: [_contratista.Id],
        centroIdsVisibles: [_centroPropio.Id]);

    /// <summary>
    /// Alcance completo dado como lista EXPLÍCITA, que es como lo recibe un Gestor CAE con una
    /// Asignación de Cartera bajo una Asignación de Operación universal (nunca null: ver
    /// <c>IAlcanceDatosService</c>).
    /// </summary>
    private AlcanceDatosServiceFalso AlcanceATodosLosCentrosComoLista() => new(
        tieneAccesoTotal: false,
        subcontrataIdsVisibles: [_subcontrata.Id],
        empresaIdsVisibles: [_contratista.Id],
        centroIdsVisibles: [_centroPropio.Id, _centroAjeno.Id]);

    /// <summary>Administrador, DireccionCae: sin restricción (null en todas las listas).</summary>
    private static AlcanceDatosServiceFalso AlcanceTotal() => new();

    private ObtenerCentrosConActividadDeSubcontrataQueryHandler CentrosDeSubcontrata(AlcanceDatosServiceFalso alcance) =>
        new(_asignaciones, _centros, _empresas, _trabajadores, alcance);

    private ObtenerCentrosConActividadDeEmpresaQueryHandler CentrosDeEmpresa(AlcanceDatosServiceFalso alcance) =>
        new(_asignaciones, _centros, _empresas, _trabajadores, alcance);

    private ObtenerSupervisionSubcontrataQueryHandler Supervision(AlcanceDatosServiceFalso alcance) =>
        new(_subcontratas, _asignaciones, _trabajadores, _centros, _empresas, _tipos, _configuracion, alcance);

    private ObtenerEvidenciaVerificacionParaDescargaQueryHandler Evidencia(AlcanceDatosServiceFalso alcance) =>
        new(_subcontratas, alcance);

    // ---- Pestaña «Centros» del panel de la subcontrata ----

    [Fact]
    public async Task Los_centros_con_actividad_de_una_subcontrata_visible_se_acotan_a_los_Centros_visibles_para_quien_pregunta()
    {
        var centros = await CentrosDeSubcontrata(AlcanceSoloAlCentroPropio())
            .Handle(new ObtenerCentrosConActividadDeSubcontrataQuery(_subcontrata.Id), CancellationToken.None);

        centros.Select(c => c.Id).Should().BeEquivalentTo([_centroPropio.Id],
            "un Centro de otro Cliente empresarial no es visible aunque la subcontrata trabaje en él");
        centros.Select(c => c.ClienteRazonSocial).Should().NotContain(_otroClienteEmpresarial.RazonSocial,
            "la razón social de otro Cliente empresarial del mismo Tenant propietario no se revela");
        centros.Single().TrabajadoresAsignados.Should().Be(1, "solo cuenta a quien trabaja en el Centro visible");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Quien_alcanza_todos_los_Centros_sigue_viendo_todos_los_centros_con_actividad_de_la_subcontrata(bool sinRestriccion)
    {
        var alcance = sinRestriccion ? AlcanceTotal() : AlcanceATodosLosCentrosComoLista();

        var centros = await CentrosDeSubcontrata(alcance)
            .Handle(new ObtenerCentrosConActividadDeSubcontrataQuery(_subcontrata.Id), CancellationToken.None);

        centros.Select(c => c.Id).Should().BeEquivalentTo([_centroPropio.Id, _centroAjeno.Id]);
        centros.Single(c => c.Id == _centroAjeno.Id).TrabajadoresAsignados.Should().Be(2);
    }

    [Fact]
    public async Task Sin_ningun_Centro_visible_la_subcontrata_visible_no_enumera_centros_con_actividad()
    {
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, subcontrataIdsVisibles: [_subcontrata.Id]);

        var centros = await CentrosDeSubcontrata(alcance)
            .Handle(new ObtenerCentrosConActividadDeSubcontrataQuery(_subcontrata.Id), CancellationToken.None);

        centros.Should().BeEmpty("una lista vacía de Centros visibles no deja pasar nada");
    }

    // ---- Consulta gemela de Empresa (sin consumidor en Web hoy; misma derivación) ----

    [Fact]
    public async Task Los_centros_con_actividad_de_una_Empresa_visible_se_acotan_a_los_Centros_visibles_para_quien_pregunta()
    {
        var centros = await CentrosDeEmpresa(AlcanceSoloAlCentroPropio())
            .Handle(new ObtenerCentrosConActividadDeEmpresaQuery(_contratista.Id), CancellationToken.None);

        centros.Select(c => c.Id).Should().BeEquivalentTo([_centroPropio.Id]);
        centros.Select(c => c.ClienteRazonSocial).Should().NotContain(_otroClienteEmpresarial.RazonSocial);
    }

    [Fact]
    public async Task Quien_no_tiene_restriccion_sigue_viendo_todos_los_centros_con_actividad_de_la_Empresa()
    {
        var centros = await CentrosDeEmpresa(AlcanceTotal())
            .Handle(new ObtenerCentrosConActividadDeEmpresaQuery(_contratista.Id), CancellationToken.None);

        centros.Select(c => c.Id).Should().BeEquivalentTo([_centroPropio.Id, _centroAjeno.Id]);
    }

    // ---- Pestaña «Supervisión» ----

    [Fact]
    public async Task La_supervision_de_una_subcontrata_visible_solo_lista_los_Centros_visibles_para_quien_pregunta()
    {
        var supervision = await Supervision(AlcanceSoloAlCentroPropio())
            .Handle(new ObtenerSupervisionSubcontrataQuery(_subcontrata.Id), CancellationToken.None);

        supervision.Should().NotBeNull("la subcontrata sigue siendo visible: este incremento no cambia eso");
        supervision!.Centros.Select(c => c.CentroId).Should().BeEquivalentTo([_centroPropio.Id],
            "ni la actividad ni una verificación ya registrada hacen visible un Centro ajeno");
        supervision.Centros.Select(c => c.ClienteRazonSocial).Should().NotContain(_otroClienteEmpresarial.RazonSocial);
        supervision.Centros.SelectMany(c => c.Tipos).Select(t => t.UltimaVerificacion?.Id)
            .Should().NotContain(_verificacionEnCentroAjeno.Id, "su identificador abre la descarga de la evidencia");
    }

    [Fact]
    public async Task La_supervision_solo_ofrece_para_verificar_los_Centros_visibles_para_quien_pregunta()
    {
        var supervision = await Supervision(AlcanceSoloAlCentroPropio())
            .Handle(new ObtenerSupervisionSubcontrataQuery(_subcontrata.Id), CancellationToken.None);

        supervision!.CentrosSeleccionables.Select(c => c.CentroId).Should().BeEquivalentTo([_centroPropio.Id]);
        supervision.CentrosSeleccionables.Select(c => c.ClienteRazonSocial).Should().NotContain(_otroClienteEmpresarial.RazonSocial);
    }

    [Fact]
    public async Task Un_Centro_ajeno_que_solo_tiene_verificaciones_tampoco_aparece_en_la_supervision()
    {
        // Sin actividad en el Centro ajeno: lo único que lo haría «relevante» es la verificación.
        _asignaciones.ListaAsignaciones.RemoveAll(a => a.CentroId == _centroAjeno.Id);

        var supervision = await Supervision(AlcanceSoloAlCentroPropio())
            .Handle(new ObtenerSupervisionSubcontrataQuery(_subcontrata.Id), CancellationToken.None);

        supervision!.Centros.Select(c => c.CentroId).Should().BeEquivalentTo([_centroPropio.Id]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Quien_alcanza_todos_los_Centros_sigue_viendo_la_supervision_completa(bool sinRestriccion)
    {
        var alcance = sinRestriccion ? AlcanceTotal() : AlcanceATodosLosCentrosComoLista();

        var supervision = await Supervision(alcance)
            .Handle(new ObtenerSupervisionSubcontrataQuery(_subcontrata.Id), CancellationToken.None);

        supervision!.Centros.Select(c => c.CentroId).Should().BeEquivalentTo([_centroPropio.Id, _centroAjeno.Id]);
        supervision.CentrosSeleccionables.Select(c => c.CentroId).Should().BeEquivalentTo([_centroPropio.Id, _centroAjeno.Id]);
        supervision.Centros.SelectMany(c => c.Tipos).Select(t => t.UltimaVerificacion?.Id)
            .Should().BeEquivalentTo(new Guid?[] { _verificacionEnCentroPropio.Id, _verificacionEnCentroAjeno.Id });
    }

    // ---- Descarga de la evidencia de una verificación, por identificador ----

    [Fact]
    public async Task La_evidencia_de_una_verificacion_en_un_Centro_no_visible_no_se_sirve_aunque_la_subcontrata_sea_visible()
    {
        var evidencia = await Evidencia(AlcanceSoloAlCentroPropio())
            .Handle(new ObtenerEvidenciaVerificacionParaDescargaQuery(_verificacionEnCentroAjeno.Id), CancellationToken.None);

        evidencia.Should().BeNull("igual que «no existe»: la supervisión tampoco enseña esa verificación");
    }

    [Fact]
    public async Task La_evidencia_de_una_verificacion_en_un_Centro_visible_se_sigue_sirviendo()
    {
        var evidencia = await Evidencia(AlcanceSoloAlCentroPropio())
            .Handle(new ObtenerEvidenciaVerificacionParaDescargaQuery(_verificacionEnCentroPropio.Id), CancellationToken.None);

        evidencia!.NombreArchivo.Should().Be("propia.pdf");
    }

    [Fact]
    public async Task Quien_no_tiene_restriccion_sigue_descargando_la_evidencia_de_cualquier_Centro()
    {
        var evidencia = await Evidencia(AlcanceTotal())
            .Handle(new ObtenerEvidenciaVerificacionParaDescargaQuery(_verificacionEnCentroAjeno.Id), CancellationToken.None);

        evidencia!.NombreArchivo.Should().Be("ajena.pdf");
    }
}
