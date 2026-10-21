using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionEmpresa;
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
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// Seguimiento de la PR #1528: un documento de la propia Empresa que el Centro exige y que no existe es pendiente de reclamación
/// aunque el Centro no lo marque «bloquea el acceso». Antes solo se pedía si lo bloqueaba (la fuente era la evaluación de acceso),
/// mientras que los de Trabajador sí salían: con un Centro que no bloquea nada la Empresa salía «al día» con documentos propios
/// por subir. La regla de exigencia es la de los pares de Empresa del Centro 360 (<c>ResolucionTipoDocumentoCentro</c>).
/// Cubre el servicio y dos de sus consumidores (el lote de Empresa y el envío a la Empresa); la preventiva y las macros, en
/// <see cref="ReclamacionEmpresaEmpleadoraTests"/> y <c>AplicarMacroCommandTests</c>. Ningún test envía correo.
/// </summary>
public class AusentesDeEmpresaEnLaReclamacionTests
{
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly ResolucionDestinatariosAgendaServiceFalso _agenda = new();
    private readonly RegistroEnvioReclamacionServiceFalso _registro = new();

    private readonly Empresa _cliente = Empresa.CrearComoCliente("Cliente SA", "B12345674", false, null, null);
    private readonly Empresa _contratista = new("Contratista SL", "B12345674");
    private readonly Centro _centro;
    private readonly Trabajador _ana;
    private readonly Asignacion _asignacion;
    private readonly TipoDocumento _plan = new("Plan de prevención", null, false, 1, AmbitoAplicacion.Empresa);

    public AusentesDeEmpresaEnLaReclamacionTests()
    {
        _centro = new Centro(_cliente.Id, _contratista.Id, "Centro Norte");
        _ana = Trabajador.DeEmpresa(_contratista.Id, "Ana", "Pérez", "00000000T");
        _asignacion = new Asignacion(_ana.Id, _centro.Id, new DateOnly(2026, 1, 1));

        _empresas.ListaEmpresas.AddRange([_cliente, _contratista]);
        _centros.ListaCentros.Add(_centro);
        _trabajadores.ListaTrabajadores.Add(_ana);
        _asignaciones.ListaAsignaciones.Add(_asignacion);
        _tipos.ListaTiposDocumento.Add(_plan);
        _agenda.RespuestaResolverParaEmpresaAsync = [new(Guid.NewGuid(), "Agenda Contratista", "agenda@contratista.example", ["Plan de prevención"])];
    }

    /// <summary>El Centro exige el plan sin marcarlo «bloquea el acceso»: el caso que antes salía «al día».</summary>
    private void ElCentroExigeSinBloquear(TipoDocumento tipo) =>
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(tipo.Id, _centro.Id, incluido: true, bloqueaAcceso: false));

    private PendientesDeReclamacionService Servicio(AlcanceDatosServiceFalso? alcance = null) =>
        PendientesDeReclamacionPruebas.Crear(_documentos, _tipos, _trabajadores, _asignaciones, _centros, _empresas, alcance ?? new AlcanceDatosServiceFalso());

    private async Task<IReadOnlyList<DocumentoPendienteDto>> PendientesDeLaContratistaAsync(AlcanceDatosServiceFalso? alcance = null) =>
        (await Servicio(alcance).ListarParaEmpresasAsync(_contratista.Id, null, CancellationToken.None))
            .Where(t => t.TitularId == _contratista.Id)
            .SelectMany(t => t.Pendientes)
            .ToList();

    private static DocumentoPendienteDto AusenteDe(TipoDocumento tipo) =>
        new(null, null, null, tipo.Id, tipo.Nombre, MotivoPendienteDeReclamacion.Ausente);

    // ---- El servicio: qué es un ausente de Empresa ----

    [Fact]
    public async Task Un_documento_de_Empresa_que_el_Centro_exige_sin_bloquear_el_acceso_y_falta_es_pendiente()
    {
        ElCentroExigeSinBloquear(_plan);

        var pendientes = await PendientesDeLaContratistaAsync();

        pendientes.Should().ContainSingle().Which.Should().Be(AusenteDe(_plan));
    }

    [Fact]
    public async Task Lo_que_el_tipo_exige_por_defecto_sin_fila_del_Centro_tambien_es_pendiente()
    {
        var porDefecto = new TipoDocumento("Certificado de la Seguridad Social", null, false, 2, AmbitoAplicacion.Empresa, RequisitoDocumental.Si);
        _tipos.ListaTiposDocumento.Add(porDefecto);

        var pendientes = await PendientesDeLaContratistaAsync();

        pendientes.Should().ContainSingle().Which.Should().Be(AusenteDe(porDefecto),
            "sin fila explícita manda el criterio global del tipo, como en el Centro 360; el plan no es obligatorio por defecto");
    }

    [Fact]
    public async Task Un_tipo_que_el_Centro_excluye_no_es_pendiente_aunque_sea_obligatorio_por_defecto()
    {
        var excluido = new TipoDocumento("Certificado de la Seguridad Social", null, false, 2, AmbitoAplicacion.Empresa, RequisitoDocumental.Si);
        _tipos.ListaTiposDocumento.Add(excluido);
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(excluido.Id, _centro.Id, incluido: false));

        (await PendientesDeLaContratistaAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Con_un_documento_operativo_del_tipo_ya_no_falta()
    {
        ElCentroExigeSinBloquear(_plan);
        _documentos.ListaDocumentos.Add(Documento.DeEmpresa(
            _contratista.Id, _plan.Id, DiaDeNegocio.Hoy().AddYears(-2), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(-5))));

        (await PendientesDeLaContratistaAsync()).Should().BeEmpty("un vencido se reclama por su vencimiento, no como ausente");
    }

    [Fact]
    public async Task Un_Centro_sin_gestion_CAE_no_exige_nada_a_la_Empresa()
    {
        ElCentroExigeSinBloquear(_plan);
        _centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);

        (await PendientesDeLaContratistaAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_ningun_Trabajador_con_Asignacion_activa_el_Centro_no_exige_nada_a_la_Empresa()
    {
        ElCentroExigeSinBloquear(_plan);
        _asignaciones.ListaAsignaciones.Clear();

        (await PendientesDeLaContratistaAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Lo_que_un_Centro_exige_a_otra_Empresa_no_se_pide_a_esta()
    {
        ElCentroExigeSinBloquear(_plan);
        var otra = new Empresa("Otra SL");
        _empresas.ListaEmpresas.Add(otra);

        (await Servicio().ListarParaEmpresasAsync(otra.Id, null, CancellationToken.None)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("empresa")]
    [InlineData("centro")]
    [InlineData("cliente")]
    [InlineData("trabajador")]
    public async Task Fuera_del_alcance_no_se_pide_nada_a_la_Empresa(string fuera)
    {
        ElCentroExigeSinBloquear(_plan);
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false,
            clienteIdsVisibles: fuera == "cliente" ? [] : [_cliente.Id],
            trabajadorIdsVisibles: fuera == "trabajador" ? [] : [_ana.Id],
            empresaIdsVisibles: [_contratista.Id, _cliente.Id],
            centroIdsVisibles: fuera == "centro" ? [] : [_centro.Id],
            empresaIdsParaGestion: fuera == "empresa" ? [] : [_contratista.Id],
            centroIdsParaGestion: [_centro.Id]);

        (await PendientesDeLaContratistaAsync(alcance)).Should().BeEmpty();
    }

    [Fact]
    public async Task Con_todo_el_alcance_visible_explicito_se_pide()
    {
        ElCentroExigeSinBloquear(_plan);
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, clienteIdsVisibles: [_cliente.Id], trabajadorIdsVisibles: [_ana.Id],
            empresaIdsVisibles: [_contratista.Id, _cliente.Id], centroIdsVisibles: [_centro.Id],
            empresaIdsParaGestion: [_contratista.Id], centroIdsParaGestion: [_centro.Id]);

        (await PendientesDeLaContratistaAsync(alcance)).Should().ContainSingle().Which.Should().Be(AusenteDe(_plan),
            "control positivo del caso anterior: lo que lo vacía es salir del alcance, no el alcance explícito");
    }

    // ---- Flujo 1: el lote de reclamación de la Empresa ----

    [Fact]
    public async Task El_lote_de_la_Empresa_ofrece_el_documento_que_falta_aunque_no_bloquee_el_acceso()
    {
        ElCentroExigeSinBloquear(_plan);
        var configuracion = new ConfiguracionQueryContextFalso();
        configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));
        var alcance = new AlcanceDatosServiceFalso();
        var handler = new ObtenerLoteReclamacionEmpresaQueryHandler(
            configuracion, _documentos, _tipos, _empresas, new ReclamacionesQueryContextFalso(), alcance, _agenda, Servicio(alcance));

        var lotes = await handler.Handle(
            new ObtenerLoteReclamacionEmpresaQuery(_contratista.Id, IncluirPendientesSinFecha: true), CancellationToken.None);

        var lote = lotes.Should().ContainSingle().Subject;
        lote.EmpresaId.Should().Be(_contratista.Id);
        lote.Documentos.Should().BeEmpty();
        lote.PendientesSinFecha.Should().ContainSingle().Which.Should().Be(AusenteDe(_plan));
        lote.Destinatarios.Should().NotBeEmpty();
    }

    // ---- Flujo 2: el envío a la Empresa lo acepta ----

    [Fact]
    public async Task El_envio_a_la_Empresa_acepta_el_documento_que_falta_aunque_no_bloquee_el_acceso()
    {
        ElCentroExigeSinBloquear(_plan);
        var alcance = new AlcanceDatosServiceFalso();
        var handler = new EnviarReclamacionEmpresaCommandHandler(_empresas, _documentos, _tipos, alcance, _agenda, _registro, Servicio(alcance));

        var resultado = await handler.Handle(
            new EnviarReclamacionEmpresaCommand(_contratista.Id, [], Pendientes: [PendienteSinFecha.Ausente(null, _plan.Id)]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : null);
        _registro.VecesLlamado.Should().Be(1);
        _registro.UltimoTitular!.Id.Should().Be(_contratista.Id);
        _registro.UltimoCuerpoHtml.Should().Contain(System.Net.WebUtility.HtmlEncode(_plan.Nombre));
    }

    [Fact]
    public async Task El_envio_a_una_Empresa_fuera_de_la_cartera_de_gestion_no_acepta_el_documento_que_falta()
    {
        ElCentroExigeSinBloquear(_plan);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: []);
        var handler = new EnviarReclamacionEmpresaCommandHandler(_empresas, _documentos, _tipos, alcance, _agenda, _registro, Servicio(alcance));

        var resultado = await handler.Handle(
            new EnviarReclamacionEmpresaCommand(_contratista.Id, [], Pendientes: [PendienteSinFecha.Ausente(null, _plan.Id)]),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        _registro.VecesLlamado.Should().Be(0);
    }
}
