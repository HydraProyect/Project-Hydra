using System.Text;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.HerramientasPdf;

/// <summary>El listado de Documentos que se pueden unir (mismo universo y misma puerta de lectura que la unión) y la lectura de un PDF subido.</summary>
public class ListarYPrepararUnionPdfTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly ProyectosQueryContextFalso _proyectos = new();
    private readonly Empresa _empresa = new("Montajes Norte S.L.", "B12345674");
    private readonly Empresa _otraEmpresa = new("Otra Empresa S.L.", "A58818501");
    private readonly Trabajador _ana;
    private readonly Trabajador _luis;
    private readonly Trabajador _deOtra;
    private readonly TipoDocumento _seguro = new("Seguro RC", vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1, AmbitoAplicacion.Empresa);
    private readonly TipoDocumento _reconocimiento = new("Reconocimiento médico", vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 2, AmbitoAplicacion.Trabajador);
    private bool _disponible = true;
    private string? _rol = "GestorCae";
    private SesionPrivilegiadaActiva? _sesionPrivilegiada;

    public ListarYPrepararUnionPdfTests()
    {
        _ana = Trabajador.DeEmpresa(_empresa.Id, "Ana", "Garcia", "12345678Z");
        _luis = Trabajador.DeEmpresa(_empresa.Id, "Luis", "Perez", "77189989B");
        _deOtra = Trabajador.DeEmpresa(_otraEmpresa.Id, "Eva", "Ruiz", "11111111H");
        _empresas.ListaEmpresas.AddRange([_empresa, _otraEmpresa]);
        _trabajadores.ListaTrabajadores.AddRange([_ana, _luis, _deOtra]);
        _tipos.ListaTiposDocumento.AddRange([_seguro, _reconocimiento]);
    }

    // ---- Listar

    [Theory]
    [InlineData("Cliente")]
    [InlineData("Lector")]
    [InlineData(null)]
    public async Task Un_rol_que_no_escribe_no_recibe_el_listado_de_Documentos(string? rol)
    {
        _rol = rol;
        _documentos.ListaDocumentos.Add(Documento.DeEmpresa(_empresa.Id, _seguro.Id, Hoy.AddDays(-5), VigenciaDocumento.NoCaduca, "seguro.pdf"));

        var resultado = await ListarAsync(_empresa.Id);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.RolSinAcceso);
    }

    [Fact]
    public async Task Una_Sesion_Privilegiada_de_plataforma_no_recibe_el_listado_de_Documentos()
    {
        _sesionPrivilegiada = new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CapacidadPrivilegio.AdminPlataforma, null);
        _documentos.ListaDocumentos.Add(Documento.DeEmpresa(_empresa.Id, _seguro.Id, Hoy.AddDays(-5), VigenciaDocumento.NoCaduca, "seguro.pdf"));

        var resultado = await ListarAsync(_empresa.Id);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.SesionPrivilegiadaSinLectura);
    }

    [Fact]
    public async Task Lista_los_Documentos_con_archivo_de_la_Empresa_y_de_sus_Trabajadores_y_ningun_otro()
    {
        var seguro = Documento.DeEmpresa(_empresa.Id, _seguro.Id, Hoy.AddDays(-5), VigenciaDocumento.VenceEl(Hoy.AddDays(100)), "seguro.pdf");
        var deAna = Documento.DeTrabajador(_ana.Id, _reconocimiento.Id, Hoy.AddDays(-20), VigenciaDocumento.NoCaduca, "ana.pdf");
        var ajenoEmpresa = Documento.DeEmpresa(_otraEmpresa.Id, _seguro.Id, Hoy.AddDays(-1), VigenciaDocumento.NoCaduca, "ajeno.pdf");
        var ajenoTrabajador = Documento.DeTrabajador(_deOtra.Id, _reconocimiento.Id, Hoy.AddDays(-1), VigenciaDocumento.NoCaduca, "eva.pdf");
        _documentos.ListaDocumentos.AddRange([seguro, deAna, ajenoEmpresa, ajenoTrabajador]);

        var lista = (await ListarAsync(_empresa.Id)).Valor.Documentos;

        lista.Select(d => d.Id).Should().Equal(seguro.Id, deAna.Id);
        lista[0].Should().Be(new DocumentoParaUnirDto(seguro.Id, "Seguro RC", null, Hoy.AddDays(-5), Hoy.AddDays(100)));
        lista[1].TitularNombre.Should().Be("Ana Garcia");
        lista[1].TipoNombre.Should().Be("Reconocimiento médico");
    }

    [Fact]
    public async Task No_ofrece_los_Documentos_de_Trabajadores_fuera_de_la_cartera_del_Gestor_CAE()
    {
        var deAna = Documento.DeTrabajador(_ana.Id, _reconocimiento.Id, Hoy.AddDays(-20), VigenciaDocumento.NoCaduca, "ana.pdf");
        var deLuis = Documento.DeTrabajador(_luis.Id, _reconocimiento.Id, Hoy.AddDays(-30), VigenciaDocumento.NoCaduca, "luis.pdf");
        var seguro = Documento.DeEmpresa(_empresa.Id, _seguro.Id, Hoy.AddDays(-5), VigenciaDocumento.NoCaduca, "seguro.pdf");
        _documentos.ListaDocumentos.AddRange([deAna, deLuis, seguro]);
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, empresaIdsParaGestion: [_empresa.Id], empresaIdsVisibles: [_empresa.Id], trabajadorIdsVisibles: [_ana.Id]);

        var lista = (await ListarAsync(_empresa.Id, alcance)).Valor.Documentos;

        lista.Select(d => d.Id).Should().BeEquivalentTo([seguro.Id, deAna.Id]);
        lista.Select(d => d.Id).Should().NotContain(deLuis.Id);
    }

    [Fact]
    public async Task Solo_lista_PDF_y_el_tope_se_aplica_despues_de_filtrar_sin_dejar_fuera_PDF_elegibles()
    {
        // Diez Documentos más recientes que no son PDF no pueden quitar sitio a los PDF del tope.
        const int tope = 500;
        _documentos.ListaDocumentos.AddRange(Enumerable.Range(0, 10).Select(i =>
            Documento.DeEmpresa(_empresa.Id, _seguro.Id, Hoy.AddDays(-1), VigenciaDocumento.NoCaduca, $"foto{i}.jpg")));
        _documentos.ListaDocumentos.AddRange(Enumerable.Range(0, tope).Select(i =>
            Documento.DeEmpresa(_empresa.Id, _seguro.Id, Hoy.AddDays(-10 - i), VigenciaDocumento.NoCaduca, $"seguro{i}.PDF")));

        var resultado = (await ListarAsync(_empresa.Id)).Valor;

        resultado.Documentos.Should().HaveCount(tope);
        resultado.HayMas.Should().BeFalse();
    }

    [Fact]
    public async Task Si_hay_mas_PDF_legibles_que_el_tope_lo_dice_para_que_la_pantalla_avise()
    {
        _documentos.ListaDocumentos.AddRange(Enumerable.Range(0, 501).Select(i =>
            Documento.DeEmpresa(_empresa.Id, _seguro.Id, Hoy.AddDays(-10 - i), VigenciaDocumento.NoCaduca, $"seguro{i}.pdf")));

        var resultado = (await ListarAsync(_empresa.Id)).Valor;

        resultado.Documentos.Should().HaveCount(500);
        resultado.HayMas.Should().BeTrue();
        resultado.Documentos[0].FechaEmision.Should().Be(Hoy.AddDays(-10), "se conservan los más recientes");
    }

    [Fact]
    public async Task Una_Empresa_gestionada_fuera_de_la_cartera_no_lista_nada_y_responde_como_inexistente()
    {
        _documentos.ListaDocumentos.Add(Documento.DeEmpresa(_empresa.Id, _seguro.Id, Hoy.AddDays(-5), VigenciaDocumento.NoCaduca, "seguro.pdf"));
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: [_otraEmpresa.Id], trabajadorIdsVisibles: []);

        var resultado = await ListarAsync(_empresa.Id, alcance);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.EmpresaNoGestionable);
    }

    [Fact]
    public async Task Listar_sin_la_herramienta_disponible_falla()
    {
        _disponible = false;

        (await ListarAsync(_empresa.Id)).Error.Codigo.Should().Be(ErroresUnirPdf.NoDisponible);
    }

    // ---- Preparar

    [Fact]
    public async Task Preparar_un_PDF_dice_cuantas_paginas_tiene_y_si_esta_firmado()
    {
        var unidor = new UnidorFijo(Result.Exito(new InfoPdfParaUnir(7, true)));

        var resultado = await Preparar(unidor, Encoding.UTF8.GetBytes("%PDF-1.4 x"), "a.pdf");

        resultado.Valor.Should().Be(new InfoPdfParaUnir(7, true));
    }

    [Theory]
    [InlineData("a.txt", "%PDF-1.4 x", ErroresUnirPdf.NoEsPdf)]
    [InlineData("a.pdf", "no es un pdf", ErroresUnirPdf.NoEsPdf)]
    [InlineData("a.pdf", "", ErroresUnirPdf.Ilegible)]
    public async Task Preparar_rechaza_lo_que_no_es_un_PDF_sin_abrirlo(string nombre, string contenido, string codigo)
    {
        var unidor = new UnidorFijo(Result.Exito(new InfoPdfParaUnir(1, false)));

        var resultado = await Preparar(unidor, Encoding.UTF8.GetBytes(contenido), nombre);

        resultado.Error.Codigo.Should().Be(codigo);
        unidor.Inspecciones.Should().Be(0);
    }

    [Fact]
    public async Task Preparar_rechaza_un_PDF_de_mas_de_diez_megas_sin_abrirlo()
    {
        var grande = new byte[CaeManager.Application.Common.LimitesArchivoSubido.TamanoMaximoBytes + 1];
        "%PDF-1.4"u8.CopyTo(grande);
        var unidor = new UnidorFijo(Result.Exito(new InfoPdfParaUnir(1, false)));

        var resultado = await Preparar(unidor, grande, "grande.pdf");

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.DemasiadoGrande);
        unidor.Inspecciones.Should().Be(0);
    }

    [Fact]
    public async Task Preparar_traslada_el_fallo_del_unidor_como_con_contrasena()
    {
        var unidor = new UnidorFijo(Result.Fallo<InfoPdfParaUnir>(Error.Crear(ErroresUnirPdf.ConContrasena, "clave")));

        var resultado = await Preparar(unidor, Encoding.UTF8.GetBytes("%PDF-1.4 x"), "a.pdf");

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.ConContrasena);
    }

    [Fact]
    public async Task Preparar_sin_la_herramienta_disponible_falla_sin_abrir_nada()
    {
        _disponible = false;
        var unidor = new UnidorFijo(Result.Exito(new InfoPdfParaUnir(1, false)));

        var resultado = await Preparar(unidor, Encoding.UTF8.GetBytes("%PDF-1.4 x"), "a.pdf");

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.NoDisponible);
        unidor.Inspecciones.Should().Be(0);
    }

    // ---- Montaje

    private Task<Result<DocumentosParaUnirDto>> ListarAsync(Guid empresaId, AlcanceDatosServiceFalso? alcance = null) =>
        new ListarDocumentosParaUnirQueryHandler(
            new Disponibilidad(_disponible), _empresas, _documentos, _trabajadores, _tipos, _proyectos, alcance ?? new AlcanceDatosServiceFalso(),
            new CurrentUserServiceFalso(rol: _rol), new SesionPrivilegiadaFija(_sesionPrivilegiada))
            .Handle(new ListarDocumentosParaUnirQuery(empresaId), CancellationToken.None);

    private Task<Result<InfoPdfParaUnir>> Preparar(UnidorFijo unidor, byte[] contenido, string nombre) =>
        new PrepararUnionPdfQueryHandler(new Disponibilidad(_disponible), unidor)
            .Handle(new PrepararUnionPdfQuery(contenido, nombre), CancellationToken.None);

    private sealed class SesionPrivilegiadaFija(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
    }

    private sealed class Disponibilidad(bool disponible) : IDisponibilidadHerramientasPdf
    {
        public ValueTask<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(disponible);
    }

    private sealed class UnidorFijo(Result<InfoPdfParaUnir> respuesta) : IUnidorPdf
    {
        public int Inspecciones { get; private set; }

        public Task<Result<InfoPdfParaUnir>> InspeccionarAsync(byte[] contenidoPdf, CancellationToken cancellationToken)
        {
            Inspecciones++;
            return Task.FromResult(respuesta);
        }

        public Task<Result<PdfUnido>> UnirAsync(IReadOnlyList<byte[]> pdfs, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
