using CaeManager.Application.Calendario.Queries;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;

namespace CaeManager.Application.Tests.Calendario;

/// <summary>
/// La vista Mes de Calendario ofrece «Reclamar» con LA regla de la Semana por Empresa
/// (<see cref="ObtenerSemanaPorEmpresaQuery"/>): aquí se prueba que el Mes la aplica igual y que no revela, ni ofrece
/// reclamar a, la Empresa empleadora de un Trabajador que el usuario ve pero cuya Empresa no está en su cartera.
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

    public VencimientosMesReclamableTests() => _configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));

    private Task<IReadOnlyList<VencimientoCalendarioDto>> Consultar(DateOnly mes, AlcanceDatosServiceFalso? alcance = null) =>
        new ObtenerVencimientosMesQueryHandler(_configuracion, _documentos, _tipos, _trabajadores, _empresas, alcance ?? new())
            .Handle(new ObtenerVencimientosMesQuery(mes.Year, mes.Month), default);

    private Empresa Empresa(string nombre = "Montajes Ebro")
    {
        var empresa = new Empresa(nombre);
        _empresas.ListaEmpresas.Add(empresa);
        return empresa;
    }

    private Trabajador TrabajadorDe(Empresa empresa, string nombre = "Ana", string dni = "77189989B", bool deSubcontrata = false)
    {
        var trabajador = deSubcontrata
            ? Trabajador.DeSubcontrata(empresa.Id, nombre, "Gil", dni)
            : Trabajador.DeEmpresa(empresa.Id, nombre, "Gil", dni);
        _trabajadores.ListaTrabajadores.Add(trabajador);
        return trabajador;
    }

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
        fila.Reclamable.Should().BeTrue("misma regla que la Semana por Empresa para un Trabajador de Empresa");
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

        var fila = (await Consultar(PrimeroDelMes, new(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id],
            trabajadorIdsVisibles: [trabajador.Id], empresaIdsParaGestion: []))).Should().ContainSingle().Which;

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
        var fila = (await Consultar(PrimeroDelMes, new(tieneAccesoTotal: false, empresaIdsVisibles: [],
            trabajadorIdsVisibles: [trabajador.Id]))).Should().ContainSingle().Which;

        fila.EmpresaId.Should().BeNull("ver a un Trabajador no revela a quién emplea");
        fila.EmpresaNombre.Should().BeNull();
        fila.Reclamable.Should().BeFalse("sin la Empresa en cartera no se ofrece reclamarle");
    }

    [Fact]
    public async Task Un_Trabajador_fuera_de_la_cartera_no_sale_en_el_mes()
    {
        var empresa = Empresa();
        DocumentoDe(TrabajadorDe(empresa), PrimeroDelMes);

        (await Consultar(PrimeroDelMes, new(tieneAccesoTotal: false, empresaIdsVisibles: [empresa.Id], trabajadorIdsVisibles: [])))
            .Should().BeEmpty("la visibilidad previa del Mes (por Trabajador) no cambia");
    }
}
