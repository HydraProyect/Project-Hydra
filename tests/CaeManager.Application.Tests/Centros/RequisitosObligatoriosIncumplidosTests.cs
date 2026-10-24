using CaeManager.Application.Centros;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// Qué pone en rojo el estado de una Empresa o de un Trabajador (decisión del propietario, 2026-10-23): solo lo que bloquea el
/// acceso, es decir, un Tipo que un Centro donde opera marca como obligatorio (<see cref="TipoDocumentoCentro.BloqueaAcceso"/>)
/// y que falta o ya no vale allí. Cada nivel pinta su contexto, y lo de un Centro no se lleva a otro.
/// </summary>
public class RequisitosObligatoriosIncumplidosTests
{
    private readonly MundoAcreditaciones _mundo = new();

    [Fact]
    public async Task Un_documento_de_Empresa_obligatorio_ausente_pone_en_rojo_a_la_Empresa_y_no_a_sus_Trabajadores()
    {
        var centro = _mundo.Centro();
        var trabajador = _mundo.Trabajador();
        _mundo.Asignacion(trabajador, centro);
        var acta = Obligatorio(AmbitoAplicacion.Empresa, "Acta de coordinación", centro);

        var deEmpresas = await Servicio().DeEmpresasAsync(Ids(_mundo.EmpresaId), CancellationToken.None);
        var deTrabajadores = await Servicio().DeTrabajadoresAsync(Ids(trabajador.Id), CancellationToken.None);

        deEmpresas.Should().ContainKey(_mundo.EmpresaId).WhoseValue.Should().ContainSingle()
            .Which.Should().Be(new RequisitoObligatorioIncumplido(
                centro.Id, acta.Id, "Acta de coordinación", SituacionDeRequisitoBloqueante.Ausente));
        deTrabajadores.Should().BeEmpty("el acta que falta es de la Empresa: no pinta en rojo a todos sus Trabajadores");
    }

    [Fact]
    public async Task Un_documento_de_Empresa_exigido_pero_no_obligatorio_no_genera_alarma()
    {
        var centro = _mundo.Centro();
        _mundo.Asignacion(_mundo.Trabajador(), centro);
        var iso = _mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Certificado ISO");
        _mundo.TiposDocumentoContexto.ListaTiposDocumentoCentros.Add(
            new TipoDocumentoCentro(iso.Id, centro.Id, incluido: true, bloqueaAcceso: false));

        var deEmpresas = await Servicio().DeEmpresasAsync(Ids(_mundo.EmpresaId), CancellationToken.None);

        deEmpresas.Should().BeEmpty("exigido sin la marca «bloquea el acceso»: no bloquea el Centro");
    }

    [Fact]
    public async Task Un_documento_de_Trabajador_obligatorio_ausente_pone_en_rojo_solo_a_ese_Trabajador()
    {
        var centro = _mundo.Centro();
        var sinApto = _mundo.Trabajador("11111111H");
        var conApto = _mundo.Trabajador("22222222J");
        _mundo.Asignacion(sinApto, centro);
        _mundo.Asignacion(conApto, centro);
        var apto = Obligatorio(AmbitoAplicacion.Trabajador, "Aptitud médica", centro);
        _mundo.DocumentoDe(conApto, apto);

        var deTrabajadores = await Servicio().DeTrabajadoresAsync(Ids(sinApto.Id, conApto.Id), CancellationToken.None);
        var deEmpresas = await Servicio().DeEmpresasAsync(Ids(_mundo.EmpresaId), CancellationToken.None);

        deTrabajadores.Keys.Should().Equal(sinApto.Id);
        RequisitosObligatoriosIncumplidos.NombresAusentes(deTrabajadores[sinApto.Id]).Should().Equal("Aptitud médica");
        deEmpresas.Should().BeEmpty("lo que le falta a un Trabajador no pinta a su Empresa");
    }

    [Fact]
    public async Task Un_documento_de_Trabajador_exigido_pero_no_obligatorio_no_genera_alarma()
    {
        var centro = _mundo.Centro();
        var trabajador = _mundo.Trabajador();
        _mundo.Asignacion(trabajador, centro);
        var formacion = _mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Trabajador, "Formación PRL");
        _mundo.TiposDocumentoContexto.ListaTiposDocumentoCentros.Add(
            new TipoDocumentoCentro(formacion.Id, centro.Id, incluido: true, bloqueaAcceso: false));

        var deTrabajadores = await Servicio().DeTrabajadoresAsync(Ids(trabajador.Id), CancellationToken.None);

        deTrabajadores.Should().BeEmpty();
    }

    [Fact]
    public async Task El_bloqueo_del_Centro_A_no_tiene_efecto_en_el_Centro_B()
    {
        var centroA = _mundo.Centro("Planta A");
        var centroB = _mundo.Centro("Planta B");
        var trabajador = _mundo.Trabajador();
        _mundo.Asignacion(trabajador, centroA);
        _mundo.Asignacion(trabajador, centroB);
        Obligatorio(AmbitoAplicacion.Empresa, "Acta de coordinación", centroA);
        Obligatorio(AmbitoAplicacion.Trabajador, "Aptitud médica", centroA);

        var deEmpresas = await Servicio().DeEmpresasAsync(Ids(_mundo.EmpresaId), CancellationToken.None);
        var deTrabajadores = await Servicio().DeTrabajadoresAsync(Ids(trabajador.Id), CancellationToken.None);

        deEmpresas[_mundo.EmpresaId].Should().OnlyContain(i => i.CentroId == centroA.Id);
        deTrabajadores[trabajador.Id].Should().OnlyContain(i => i.CentroId == centroA.Id);

        var soloB = await new EvaluacionDeAccesoPorCentroService(
                _mundo.CentrosContexto, _mundo.TiposDocumentoContexto, _mundo.TrabajadoresContexto,
                _mundo.AsignacionesContexto, _mundo.DocumentosContexto, new AlcanceDatosServiceFalso())
            .EvaluarAsync([centroB.Id], CancellationToken.None);
        soloB.Requisitos.Should().BeEmpty("el Centro B no exige nada: lo que bloquea en A no bloquea en B");
    }

    [Fact]
    public async Task Solo_cuentan_los_Centros_visibles_para_el_usuario()
    {
        var visible = _mundo.Centro("Planta visible");
        var ajeno = _mundo.Centro("Planta ajena");
        var trabajador = _mundo.Trabajador();
        _mundo.Asignacion(trabajador, visible);
        _mundo.Asignacion(trabajador, ajeno);
        Obligatorio(AmbitoAplicacion.Empresa, "Acta de coordinación", ajeno);
        Obligatorio(AmbitoAplicacion.Trabajador, "Aptitud médica", ajeno);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [visible.Id]);

        var deEmpresas = await Servicio(alcance).DeEmpresasAsync(Ids(_mundo.EmpresaId), CancellationToken.None);
        var deTrabajadores = await Servicio(alcance).DeTrabajadoresAsync(Ids(trabajador.Id), CancellationToken.None);

        deEmpresas.Should().BeEmpty("quien no alcanza un Centro no aprende por el estado de la Empresa qué exige ese Centro");
        deTrabajadores.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_documento_obligatorio_vencido_cuenta_y_uno_vigente_no()
    {
        var centro = _mundo.Centro();
        _mundo.Asignacion(_mundo.Trabajador(), centro);
        var acta = Obligatorio(AmbitoAplicacion.Empresa, "Acta de coordinación", centro);
        var documento = Documento.DeEmpresa(
            _mundo.EmpresaId, acta.Id, DiaDeNegocio.Hoy().AddYears(-2), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(-10)));
        _mundo.DocumentosContexto.ListaDocumentos.Add(documento);

        var vencido = await Servicio().DeEmpresasAsync(Ids(_mundo.EmpresaId), CancellationToken.None);
        vencido[_mundo.EmpresaId].Should().ContainSingle().Which.Situacion.Should().Be(SituacionDeRequisitoBloqueante.Vencido);
        RequisitosObligatoriosIncumplidos.NombresAusentes(vencido[_mundo.EmpresaId]).Should().BeEmpty(
            "el motivo «Falta» es solo de lo que no tiene ningún documento");

        _mundo.DocumentoDeEmpresa(acta);
        (await Servicio().DeEmpresasAsync(Ids(_mundo.EmpresaId), CancellationToken.None)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(SituacionDeRequisitoBloqueante.Ausente, true)]
    [InlineData(SituacionDeRequisitoBloqueante.Vencido, true)]
    [InlineData(SituacionDeRequisitoBloqueante.Cumplido, false)]
    [InlineData(SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma, false)]
    [InlineData(SituacionDeRequisitoBloqueante.SinValidarEnPlataforma, false)]
    public void Solo_ausente_o_vencido_ponen_en_rojo(SituacionDeRequisitoBloqueante situacion, bool cuenta) =>
        RequisitosObligatoriosIncumplidos.CuentaParaElEstado(situacion).Should().Be(cuenta);

    private TipoDocumento Obligatorio(AmbitoAplicacion ambito, string nombre, Centro centro)
    {
        var tipo = _mundo.Tipo(RequisitoDocumental.Si, ambito, nombre);
        _mundo.TiposDocumentoContexto.ListaTiposDocumentoCentros.Add(
            new TipoDocumentoCentro(tipo.Id, centro.Id, incluido: true, bloqueaAcceso: true));
        return tipo;
    }

    private static IQueryable<Guid> Ids(params Guid[] ids) => ids.AsQueryable();

    private RequisitosObligatoriosIncumplidosService Servicio(AlcanceDatosServiceFalso? alcance = null) =>
        new(
            new EvaluacionDeAccesoPorCentroService(
                _mundo.CentrosContexto, _mundo.TiposDocumentoContexto, _mundo.TrabajadoresContexto,
                _mundo.AsignacionesContexto, _mundo.DocumentosContexto, alcance ?? new AlcanceDatosServiceFalso()),
            _mundo.AsignacionesContexto, _mundo.TrabajadoresContexto, _mundo.TiposDocumentoContexto);
}
