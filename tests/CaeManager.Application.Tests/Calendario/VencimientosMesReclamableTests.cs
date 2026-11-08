using CaeManager.Application.Calendario.Queries;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;

namespace CaeManager.Application.Tests.Calendario;

/// <summary>
/// La vista Mes de Calendario ofrece «Reclamar» con LA regla de la Semana por Empresa
/// (<see cref="ObtenerSemanaPorEmpresaQuery"/>): aquí se prueba que el Mes la aplica igual y que no revela, ni ofrece
/// reclamar a, la Empresa empleadora de un Trabajador que el usuario ve pero cuya Empresa no está en su cartera. Además exige,
/// como el lote que abre el cajón, una Asignación activa a un Centro visible con gestión CAE (<c>AsignacionesReclamables</c>).
/// </summary>
public class VencimientosMesReclamableTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();
    private static readonly DateOnly PrimeroDelMes = new(Hoy.Year, Hoy.Month, 1);

    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly ProyectosQueryContextFalso _proyectos = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly CentrosQueryContextFalso _centros = new();

    // El Cliente empresarial y el Centro con gestión CAE donde trabajan, por defecto, los Trabajadores de estos tests: la
    // Asignación reclamable del lote (AsignacionesReclamables).
    private readonly Empresa _clienteDelCentro = CaeManager.Domain.Empresas.Empresa.CrearComoCliente("Cliente SA", "B12345674", false, null, null);
    private readonly Centro _centro;

    public VencimientosMesReclamableTests()
    {
        _configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));
        _empresas.ListaEmpresas.Add(_clienteDelCentro);
        _centro = new Centro(_clienteDelCentro.Id, Guid.NewGuid(), "Centro Norte");
        _centros.ListaCentros.Add(_centro);
    }

    private Task<IReadOnlyList<VencimientoCalendarioDto>> Consultar(DateOnly mes, AlcanceDatosServiceFalso? alcance = null) =>
        new ObtenerVencimientosMesQueryHandler(_configuracion, _documentos, _tipos, _trabajadores, _empresas, _proyectos, _asignaciones, _centros, alcance ?? new())
            .Handle(new ObtenerVencimientosMesQuery(mes.Year, mes.Month), default);

    private Empresa Empresa(string nombre = "Montajes Ebro")
    {
        var empresa = new Empresa(nombre);
        _empresas.ListaEmpresas.Add(empresa);
        return empresa;
    }

    private Trabajador TrabajadorDe(Empresa empresa, string nombre = "Ana", string dni = "77189989B", bool deSubcontrata = false, Centro? asignadoA = null, bool sinAsignacion = false)
    {
        var trabajador = deSubcontrata
            ? Trabajador.DeSubcontrata(empresa.Id, nombre, "Gil", dni)
            : Trabajador.DeEmpresa(empresa.Id, nombre, "Gil", dni);
        _trabajadores.ListaTrabajadores.Add(trabajador);
        if (!sinAsignacion)
            _asignaciones.ListaAsignaciones.Add(new Asignacion(trabajador.Id, (asignadoA ?? _centro).Id, Hoy.AddMonths(-6)));
        return trabajador;
    }

    /// <summary>Alcance restringido que ve el Centro y su Cliente empresarial: lo que exige la Asignación reclamable.</summary>
    private AlcanceDatosServiceFalso Alcance(IReadOnlyList<Guid> trabajadorIdsVisibles, IReadOnlyList<Guid>? empresaIdsVisibles = null,
        IReadOnlyList<Guid>? clienteIdsVisibles = null, IReadOnlyList<Guid>? subcontrataIdsVisibles = null,
        IReadOnlyList<Guid>? empresaIdsParaGestion = null, IReadOnlyList<Guid>? centroIdsVisibles = null) =>
        new(tieneAccesoTotal: false, empresaIdsVisibles: empresaIdsVisibles ?? [], trabajadorIdsVisibles: trabajadorIdsVisibles,
            clienteIdsVisibles: [.. clienteIdsVisibles ?? [], _clienteDelCentro.Id], subcontrataIdsVisibles: subcontrataIdsVisibles,
            empresaIdsParaGestion: empresaIdsParaGestion, centroIdsVisibles: centroIdsVisibles ?? [_centro.Id]);

    private Documento DocumentoDe(Trabajador trabajador, DateOnly vence, AmbitoAplicacion ambito = AmbitoAplicacion.Trabajador)
    {
        var tipo = new TipoDocumento("Formación PRL", 12, false, 1, ambito);
        _tipos.ListaTiposDocumento.Add(tipo);
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, vence.AddYears(-1), VigenciaDocumento.VenceEl(vence));
        _documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    [Fact]
    public async Task El_documento_de_un_Trabajador_de_Empresa_sale_reclamable_con_su_Empresa_empleadora()
    {
        var empresa = Empresa();
        var documento = DocumentoDe(TrabajadorDe(empresa), PrimeroDelMes);

        var fila = (await Consultar(PrimeroDelMes)).Should().ContainSingle().Which;

        fila.DocumentoId.Should().Be(documento.Id);
        fila.EmpresaId.Should().Be(empresa.Id);
        fila.EmpresaNombre.Should().Be("Montajes Ebro");
        fila.Reclamable.Should().BeTrue("misma regla que la Semana por Empresa y Asignación activa a un Centro con gestión CAE, como el lote");
    }

    [Fact]
    public async Task Sin_Asignacion_activa_a_un_Centro_se_ve_pero_no_es_reclamable_porque_el_cajon_lo_descartaria()
    {
        var empresa = Empresa();
        DocumentoDe(TrabajadorDe(empresa, sinAsignacion: true), PrimeroDelMes);

        var fila = (await Consultar(PrimeroDelMes)).Should().ContainSingle().Which;

        fila.EmpresaId.Should().Be(empresa.Id, "el vencimiento y su Empresa siguen visibles");
        fila.Reclamable.Should().BeFalse("el lote de reclamación exige una Asignación activa (AsignacionesReclamables)");
    }

    [Fact]
    public async Task Con_la_Asignacion_dada_de_baja_no_es_reclamable()
    {
        var empresa = Empresa();
        var trabajador = TrabajadorDe(empresa);
        _asignaciones.ListaAsignaciones.Single(a => a.TrabajadorId == trabajador.Id).DarDeBaja(Hoy.AddDays(-1));
        DocumentoDe(trabajador, PrimeroDelMes);

        (await Consultar(PrimeroDelMes)).Should().ContainSingle().Which.Reclamable.Should().BeFalse();
    }

    [Fact]
    public async Task En_un_Centro_sin_gestion_CAE_no_es_reclamable()
    {
        var empresa = Empresa();
        var sinGestion = new Centro(_clienteDelCentro.Id, Guid.NewGuid(), "Centro sin gestión");
        sinGestion.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        _centros.ListaCentros.Add(sinGestion);
        DocumentoDe(TrabajadorDe(empresa, asignadoA: sinGestion), PrimeroDelMes);

        (await Consultar(PrimeroDelMes)).Should().ContainSingle().Which.Reclamable.Should().BeFalse();
    }

    [Theory]
    [InlineData("Centro")]
    [InlineData("Cliente")]
    public async Task Con_el_Centro_o_su_Cliente_empresarial_fuera_del_alcance_no_es_reclamable(string fuera)
    {
        var empresa = Empresa();
        var trabajador = TrabajadorDe(empresa);
        DocumentoDe(trabajador, PrimeroDelMes);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id],
            trabajadorIdsVisibles: [trabajador.Id], empresaIdsParaGestion: [empresa.Id],
            centroIdsVisibles: fuera == "Centro" ? [] : [_centro.Id],
            clienteIdsVisibles: fuera == "Cliente" ? [] : [_clienteDelCentro.Id]);

        var fila = (await Consultar(PrimeroDelMes, alcance)).Should().ContainSingle().Which;

        fila.EmpresaId.Should().Be(empresa.Id);
        fila.Reclamable.Should().BeFalse($"el lote no recorre una Asignación cuyo {fuera} no es visible");
    }

    [Fact]
    public async Task Con_el_Centro_y_su_Cliente_empresarial_en_el_alcance_si_es_reclamable()
    {
        // El control de la teoría de arriba: el mismo alcance con los dos visibles.
        var empresa = Empresa();
        var trabajador = TrabajadorDe(empresa);
        DocumentoDe(trabajador, PrimeroDelMes);

        (await Consultar(PrimeroDelMes, Alcance([trabajador.Id], empresaIdsVisibles: [empresa.Id], empresaIdsParaGestion: [empresa.Id])))
            .Should().ContainSingle().Which.Reclamable.Should().BeTrue();
    }

    [Fact]
    public async Task Un_voluntario_activo_es_reclamable()
    {
        var empresa = Empresa();
        var voluntario = TrabajadorDe(empresa);
        voluntario.CambiarTipoContratacion(TipoContratacion.Voluntario);
        // Activo mientras participa en un Evento sin terminar (ActividadVoluntarios).
        var evento = Proyecto.Crear(_clienteDelCentro.Id, _centro.Id, "Evento Demo", Hoy.AddDays(-1), Hoy.AddDays(30), null, TipoProyecto.Evento);
        _proyectos.ListaProyectos.Add(evento);
        _proyectos.ListaProyectosTecnicos.Add(new ProyectoTecnico(evento.Id, voluntario.Id, Hoy.AddDays(-1)));
        DocumentoDe(voluntario, PrimeroDelMes);

        (await Consultar(PrimeroDelMes)).Should().ContainSingle().Which.Reclamable.Should().BeTrue();
    }

    [Fact]
    public async Task Dos_Empresas_en_el_mismo_mes_salen_cada_una_con_su_Empresa()
    {
        var una = Empresa("Montajes Ebro");
        var otra = Empresa("Estructuras Duero");
        var deUna = DocumentoDe(TrabajadorDe(una, "Ana", "77189989B"), PrimeroDelMes);
        var deOtra = DocumentoDe(TrabajadorDe(otra, "Luis", "12345678Z"), PrimeroDelMes);

        var filas = await Consultar(PrimeroDelMes);

        filas.Single(f => f.DocumentoId == deUna.Id).EmpresaId.Should().Be(una.Id);
        filas.Single(f => f.DocumentoId == deOtra.Id).EmpresaId.Should().Be(otra.Id);
        filas.Should().OnlyContain(f => f.Reclamable);
    }

    [Fact]
    public async Task El_documento_de_un_Trabajador_de_Subcontrata_no_es_reclamable_porque_va_al_Cliente()
    {
        var empresa = Empresa();
        DocumentoDe(TrabajadorDe(empresa, deSubcontrata: true), PrimeroDelMes);

        var fila = (await Consultar(PrimeroDelMes)).Should().ContainSingle().Which;

        fila.EmpresaId.Should().Be(empresa.Id, "la fila es la Empresa empleadora (SubcontrataId ?? EmpresaId)");
        fila.Reclamable.Should().BeFalse();
    }

    [Fact]
    public async Task Un_tipo_de_ambito_Empresa_no_es_reclamable_desde_la_fila_de_un_Trabajador()
    {
        var empresa = Empresa();
        DocumentoDe(TrabajadorDe(empresa), PrimeroDelMes, AmbitoAplicacion.Empresa);

        (await Consultar(PrimeroDelMes)).Should().ContainSingle().Which.Reclamable.Should().BeFalse();
    }

    [Fact]
    public async Task Fuera_de_la_ventana_de_reclamacion_no_es_reclamable()
    {
        var empresa = Empresa();
        var lejano = new DateOnly(Hoy.Year, Hoy.Month, 1).AddMonths(VentanaReclamacion.Meses + 2);
        DocumentoDe(TrabajadorDe(empresa), lejano);

        (await Consultar(lejano)).Should().ContainSingle().Which.Reclamable.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_alcance_de_gestion_sobre_la_Empresa_no_es_reclamable_aunque_se_vea()
    {
        var empresa = Empresa();
        var trabajador = TrabajadorDe(empresa);
        DocumentoDe(trabajador, PrimeroDelMes);

        var fila = (await Consultar(PrimeroDelMes, Alcance([trabajador.Id], empresaIdsVisibles: [empresa.Id],
            empresaIdsParaGestion: []))).Should().ContainSingle().Which;

        fila.EmpresaId.Should().Be(empresa.Id);
        fila.Reclamable.Should().BeFalse();
    }

    [Fact]
    public async Task Un_usuario_fuera_de_la_cartera_de_la_Empresa_ve_el_vencimiento_pero_ni_la_Empresa_ni_Reclamar()
    {
        var empresa = Empresa();
        var trabajador = TrabajadorDe(empresa);
        DocumentoDe(trabajador, PrimeroDelMes);

        // Ve al Trabajador, pero la Empresa que lo emplea no está en ningún alcance suyo (ni Empresa, ni Cliente, ni Subcontrata).
        var fila = (await Consultar(PrimeroDelMes, Alcance([trabajador.Id]))).Should().ContainSingle().Which;

        fila.EmpresaId.Should().BeNull("ver a un Trabajador no revela a quién emplea");
        fila.EmpresaNombre.Should().BeNull();
        fila.Reclamable.Should().BeFalse("sin la Empresa en cartera no se ofrece reclamarle");
    }

    [Fact]
    public async Task Gestion_sobre_una_Empresa_que_no_esta_en_el_alcance_de_lectura_tampoco_la_hace_reclamable()
    {
        var empresa = Empresa();
        var trabajador = TrabajadorDe(empresa);
        DocumentoDe(trabajador, PrimeroDelMes);

        // Defensa en profundidad: la Semana oculta la fila entera si la Empresa empleadora no está en ningún alcance de lectura.
        var fila = (await Consultar(PrimeroDelMes, Alcance([trabajador.Id], empresaIdsParaGestion: [empresa.Id])))
            .Should().ContainSingle().Which;

        fila.EmpresaId.Should().BeNull();
        fila.Reclamable.Should().BeFalse();
    }

    [Fact]
    public async Task Un_Trabajador_fuera_de_la_cartera_no_sale_en_el_mes()
    {
        var empresa = Empresa();
        DocumentoDe(TrabajadorDe(empresa), PrimeroDelMes);

        (await Consultar(PrimeroDelMes, Alcance([], empresaIdsVisibles: [empresa.Id])))
            .Should().BeEmpty("la visibilidad previa del Mes (por Trabajador) no cambia");
    }

    [Fact]
    public async Task Un_voluntario_inactivo_se_ve_en_el_mes_pero_no_es_reclamable()
    {
        var empresa = Empresa();
        var voluntario = TrabajadorDe(empresa);
        voluntario.CambiarTipoContratacion(TipoContratacion.Voluntario);
        DocumentoDe(voluntario, PrimeroDelMes);

        var fila = (await Consultar(PrimeroDelMes)).Should().ContainSingle().Which;

        fila.EmpresaId.Should().Be(empresa.Id, "el vencimiento sigue visible");
        fila.Reclamable.Should().BeFalse("la Semana oculta a los voluntarios inactivos y el envío los rechaza");
    }

    [Fact]
    public async Task Una_Empresa_visible_solo_por_su_Cliente_empresarial_sale_con_nombre_y_reclamable_si_hay_gestion()
    {
        var empresa = Empresa();
        var trabajador = TrabajadorDe(empresa);
        DocumentoDe(trabajador, PrimeroDelMes);

        var fila = (await Consultar(PrimeroDelMes, Alcance([trabajador.Id], clienteIdsVisibles: [empresa.Id],
            empresaIdsParaGestion: [empresa.Id]))).Should().ContainSingle().Which;

        fila.EmpresaId.Should().Be(empresa.Id);
        fila.Reclamable.Should().BeTrue();
    }

    [Fact]
    public async Task Una_Empresa_visible_solo_como_Subcontrata_sale_con_nombre_pero_no_reclamable_sin_gestion()
    {
        var empresa = Empresa();
        var trabajador = TrabajadorDe(empresa);
        DocumentoDe(trabajador, PrimeroDelMes);

        var fila = (await Consultar(PrimeroDelMes, Alcance([trabajador.Id], subcontrataIdsVisibles: [empresa.Id],
            empresaIdsParaGestion: []))).Should().ContainSingle().Which;

        fila.EmpresaId.Should().Be(empresa.Id);
        fila.Reclamable.Should().BeFalse();
    }
}