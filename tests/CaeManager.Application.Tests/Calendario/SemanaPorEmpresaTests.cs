using CaeManager.Application.Calendario.Queries;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;

namespace CaeManager.Application.Tests.Calendario;

public class SemanaPorEmpresaTests
{
    private static readonly DateOnly Lunes = new(2026, 9, 28);
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly Historial _historial = new();
    private readonly MediatorFalso _mediador = new() { Respuesta = Array.Empty<DocumentacionBloqueantePendienteDto>() };

    private ObtenerSemanaPorEmpresaQueryHandler Handler(AlcanceDatosServiceFalso? alcance = null) =>
        new(_documentos, _empresas, _trabajadores, new ProyectosQueryContextFalso(), _historial, _tipos, alcance ?? new(), _mediador);

    private Empresa Empresa(string nombre = "Montajes Ebro")
    {
        var empresa = new Empresa(nombre);
        _empresas.ListaEmpresas.Add(empresa);
        return empresa;
    }

    private Documento Documento(Guid sujeto, DateOnly fecha, bool trabajador = false)
    {
        var tipo = new TipoDocumento("Formación PRL", 12, false, 1,
            trabajador ? AmbitoAplicacion.Trabajador : AmbitoAplicacion.Empresa);
        _tipos.ListaTiposDocumento.Add(tipo);
        var documento = trabajador
            ? CaeManager.Domain.Documentos.Documento.DeTrabajador(sujeto, tipo.Id, fecha.AddYears(-1), VigenciaDocumento.VenceEl(fecha))
            : CaeManager.Domain.Documentos.Documento.DeEmpresa(sujeto, tipo.Id, fecha.AddYears(-1), VigenciaDocumento.VenceEl(fecha));
        _documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    [Fact]
    public async Task Semana_completa_entre_meses_agrupa_por_id_y_excluye_el_fin_de_semana()
    {
        var una = Empresa();
        var otra = Empresa();
        var trabajador = Trabajador.DeEmpresa(una.Id, "Ana", "Gil", "77189989B");
        _trabajadores.ListaTrabajadores.Add(trabajador);
        var lunes = Documento(una.Id, Lunes);
        var viernes = Documento(trabajador.Id, Lunes.AddDays(4), true);
        var duplicada = Documento(otra.Id, Lunes.AddDays(2));
        Documento(una.Id, Lunes.AddDays(-1));
        Documento(una.Id, Lunes.AddDays(5));
        Documento(una.Id, Lunes.AddDays(7));

        var resultado = await Handler().Handle(new(Lunes.AddDays(6)), default);

        resultado.Should().HaveCount(2);
        resultado.Single(e => e.EmpresaId == una.Id).Documentos.Select(d => d.DocumentoId)
            .Should().Equal(lunes.Id, viernes.Id);
        resultado.Single(e => e.EmpresaId == otra.Id).Documentos.Should().ContainSingle(d => d.DocumentoId == duplicada.Id);
    }

    [Fact]
    public async Task Ver_un_trabajador_no_concede_ni_los_documentos_ni_la_fila_de_su_empresa()
    {
        var empresa = Empresa();
        var trabajador = Trabajador.DeSubcontrata(empresa.Id, "Ana", "Gil", "77189989B");
        _trabajadores.ListaTrabajadores.Add(trabajador);
        Documento(empresa.Id, Lunes);
        Documento(trabajador.Id, Lunes, true);

        var resultado = await Handler(new(tieneAccesoTotal: false, empresaIdsVisibles: [], trabajadorIdsVisibles: [trabajador.Id]))
            .Handle(new(Lunes), default);

        resultado.Should().BeEmpty("la Empresa empleadora no está en ningún alcance del usuario: ver al Trabajador no la revela");
    }

    [Fact]
    public async Task La_empresa_de_un_trabajador_visible_solo_como_subcontrata_sale_sin_sus_documentos_de_empresa()
    {
        var empresa = Empresa();
        var trabajador = Trabajador.DeSubcontrata(empresa.Id, "Ana", "Gil", "77189989B");
        _trabajadores.ListaTrabajadores.Add(trabajador);
        Documento(empresa.Id, Lunes);
        var visible = Documento(trabajador.Id, Lunes, true);

        var resultado = await Handler(new(tieneAccesoTotal: false, empresaIdsVisibles: [], trabajadorIdsVisibles: [trabajador.Id],
            subcontrataIdsVisibles: [empresa.Id])).Handle(new(Lunes), default);

        var fila = resultado.Should().ContainSingle().Which;
        fila.Documentos.Should().ContainSingle(d => d.DocumentoId == visible.Id);
        fila.EsSubcontrata.Should().BeTrue("su ficha es /subcontratas/{id}");
    }

    [Fact]
    public async Task La_empresa_de_un_trabajador_visible_solo_como_cliente_empresarial_tambien_sale()
    {
        var empresa = Empresa();
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "Gil", "77189989B");
        _trabajadores.ListaTrabajadores.Add(trabajador);
        Documento(trabajador.Id, Lunes, true);

        var resultado = await Handler(new(tieneAccesoTotal: false, empresaIdsVisibles: [], trabajadorIdsVisibles: [trabajador.Id],
            clienteIdsVisibles: [empresa.Id])).Handle(new(Lunes), default);

        resultado.Should().ContainSingle().Which.EsSubcontrata.Should().BeFalse();
    }

    [Fact]
    public async Task Solo_es_reclamable_lo_que_se_puede_pedir_a_los_contactos_de_la_empresa()
    {
        var empresa = Empresa();
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "Gil", "77189989B");
        _trabajadores.ListaTrabajadores.Add(trabajador);
        var propio = Documento(empresa.Id, Lunes);
        var deTrabajador = Documento(trabajador.Id, Lunes, true);

        var fila = (await Handler().Handle(new(Lunes), default)).Single();

        fila.Documentos.Single(d => d.DocumentoId == propio.Id).Should().Match<DocumentoSemanaDto>(d => d.Reclamable && !d.DeTrabajador);
        fila.Documentos.Single(d => d.DocumentoId == deTrabajador.Id).Should().Match<DocumentoSemanaDto>(d => !d.Reclamable && d.DeTrabajador,
            "el documento de un Trabajador se reclama al Cliente del Centro, no a la Empresa");
    }

    [Fact]
    public async Task Fuera_de_la_ventana_de_reclamacion_no_es_reclamable()
    {
        var empresa = Empresa();
        var lejano = DiaDeNegocio.Hoy().AddMonths(VentanaReclamacion.Meses + 2);
        lejano = lejano.AddDays(-((int)lejano.DayOfWeek + 6) % 7);
        var documento = Documento(empresa.Id, lejano);

        var fila = (await Handler().Handle(new(lejano), default)).Single();

        fila.Documentos.Single(d => d.DocumentoId == documento.Id).Reclamable.Should().BeFalse();
        fila.ReclamadoHoy.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_alcance_de_gestion_sobre_la_empresa_no_es_reclamable()
    {
        var empresa = Empresa();
        var documento = Documento(empresa.Id, Lunes);

        var fila = (await Handler(new(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id], empresaIdsParaGestion: []))
            .Handle(new(Lunes), default)).Single();

        fila.Documentos.Single(d => d.DocumentoId == documento.Id).Reclamable.Should().BeFalse();
    }
    [Fact]
    public async Task Cartera_vacia_no_revela_filas_ni_consulta_los_bloqueos()
    {
        Documento(Empresa().Id, Lunes);
        var resultado = await Handler(new(tieneAccesoTotal: false)).Handle(new(Lunes), default);
        resultado.Should().BeEmpty();
        _mediador.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Solo_el_requisito_bloqueante_del_mismo_sujeto_y_tipo_marca_el_documento()
    {
        var empresa = Empresa();
        var bloqueante = Documento(empresa.Id, Lunes);
        var otroTipo = Documento(empresa.Id, Lunes);
        _mediador.Respuesta = new[] { new DocumentacionBloqueantePendienteDto(
            Guid.NewGuid(), "Centro", Guid.NewGuid(), "Ana", bloqueante.TipoDocumentoId, "Formación PRL",
            EmpresaId: empresa.Id, Ambito: AmbitoAplicacion.Empresa) };

        var resultado = await Handler().Handle(new(Lunes), default);

        resultado.Single().Documentos.Single(d => d.DocumentoId == bloqueante.Id).BloqueaAcceso.Should().BeTrue();
        resultado.Single().Documentos.Single(d => d.DocumentoId == otroTipo.Id).BloqueaAcceso.Should().BeFalse();
    }

    [Fact]
    public async Task Un_envio_parcial_no_afirma_que_se_reclamo_toda_la_fila()
    {
        var empresa = Empresa();
        var uno = Documento(empresa.Id, Lunes);
        var dos = Documento(empresa.Id, Lunes.AddDays(1));
        var hoy = DiaDeNegocio.Hoy();
        _historial.Envios.Add(ReclamacionDocumental.ParaEmpresa(empresa.Id, Guid.NewGuid(), "prl@example.invalid",
            DiaDeNegocio.InicioEnUtc(hoy), [uno.Id]));
        (await Handler().Handle(new(Lunes), default)).Single().ReclamadoHoy.Should().BeFalse();

        _historial.Envios.Add(ReclamacionDocumental.ParaEmpresa(empresa.Id, Guid.NewGuid(), "prl@example.invalid",
            DiaDeNegocio.InicioEnUtc(hoy.AddDays(1)).AddTicks(-1), [dos.Id]));
        (await Handler().Handle(new(Lunes), default)).Single().ReclamadoHoy.Should().BeTrue();
    }

    [Fact]
    public async Task El_historial_de_ayer_y_manana_no_es_reclamado_hoy()
    {
        var empresa = Empresa();
        var documento = Documento(empresa.Id, Lunes);
        var hoy = DiaDeNegocio.Hoy();
        foreach (var instante in new[] { DiaDeNegocio.InicioEnUtc(hoy).AddTicks(-1), DiaDeNegocio.InicioEnUtc(hoy.AddDays(1)) })
            _historial.Envios.Add(ReclamacionDocumental.ParaEmpresa(empresa.Id, Guid.NewGuid(), "prl@example.invalid", instante, [documento.Id]));

        (await Handler().Handle(new(Lunes), default)).Single().ReclamadoHoy.Should().BeFalse();
    }

    private sealed class Historial : IReclamacionesQueryContext
    {
        public List<ReclamacionDocumental> Envios { get; } = [];
        public IQueryable<ReclamacionDocumental> ReclamacionesDocumentales => new TestAsyncQueryable<ReclamacionDocumental>(Envios.AsQueryable());
        public IQueryable<ReclamacionDocumentalDocumento> ReclamacionesDocumentalesDocumento =>
            new TestAsyncQueryable<ReclamacionDocumentalDocumento>(Envios.SelectMany(e => e.Documentos).AsQueryable());
    }
}
