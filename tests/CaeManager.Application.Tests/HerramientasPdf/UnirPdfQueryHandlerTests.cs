using System.Text;
using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Configuracion;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.HerramientasPdf;

/// <summary>
/// «Unir PDF»: el orden de la lista es el del PDF unido, los topes (20 ficheros, 10 MB por subido, 50 MB en total, 500
/// páginas) se aplican, y lo que sale de Documentos pasa por el alcance de cartera del Gestor CAE, el rol y el registro de
/// acceso de DEC-36. El PDF firmado avisa y no se une hasta confirmar.
/// </summary>
public class UnirPdfQueryHandlerTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly ProyectosQueryContextFalso _proyectos = new();
    private readonly FileStorageServiceFalso _almacenamiento = new();
    private readonly UnidorPdfFalso _unidor = new();
    private readonly TransaccionConEstado _transaccion = new();
    private readonly RegistroQueAnota _registro;

    private readonly Empresa _empresa = new("Montajes Norte S.L.", "B12345674");
    private readonly Empresa _otraEmpresa = new("Otra Empresa S.L.", "A58818501");
    private readonly Trabajador _ana;

    private AlcanceDatosServiceFalso _alcance;
    private string? _rol = "GestorCae";
    private SesionPrivilegiadaActiva? _sesionPrivilegiada;
    private bool _disponible = true;
    private Guid _tenantActual;

    public UnirPdfQueryHandlerTests()
    {
        _tenantActual = _tenantId;
        _registro = new RegistroQueAnota(_transaccion);
        _alcance = new AlcanceDatosServiceFalso();
        _ana = Trabajador.DeEmpresa(_empresa.Id, "Ana", "Garcia", "12345678Z");
        _empresas.ListaEmpresas.AddRange([_empresa, _otraEmpresa]);
        _trabajadores.ListaTrabajadores.Add(_ana);
    }

    // ---- Orden

    [Fact]
    public async Task Une_los_PDF_subidos_en_el_orden_de_la_lista_y_nombra_el_resultado_con_la_Empresa_gestionada()
    {
        var resultado = await UnirAsync([Subido("c.pdf", "C"), Subido("a.pdf", "A"), Subido("b.pdf", "B")]);

        resultado.EsFallido.Should().BeFalse();
        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
        _unidor.Unidos.Should().Equal("C", "A", "B");
        resultado.Valor.NumeroPaginas.Should().Be(3);
        resultado.Valor.NombreArchivo.Should().Be($"Montajes-Norte-S.L_unido_{Hoy:yyyy-MM-dd}.pdf", "el saneado quita el punto final de la razón social");
        resultado.Valor.Contenido.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Mezcla_subidos_y_Documentos_sin_alterar_el_orden_pedido()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");
        var reconocimiento = DocumentoDeTrabajador(_ana, "RECONOCIMIENTO");

        var resultado = await UnirAsync([
            new EntradaUnionDocumento(reconocimiento.Id), Subido("carta.pdf", "CARTA"), new EntradaUnionDocumento(seguro.Id)]);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
        _unidor.Unidos.Should().Equal("RECONOCIMIENTO", "CARTA", "SEGURO");
    }

    [Fact]
    public async Task Un_Documento_repetido_en_la_lista_se_une_las_veces_que_aparece_y_se_registra_una_sola_vez()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");

        var resultado = await UnirAsync([new EntradaUnionDocumento(seguro.Id), Subido("a.pdf", "A"), new EntradaUnionDocumento(seguro.Id)]);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
        _unidor.Unidos.Should().Equal("SEGURO", "A", "SEGURO");
        _registro.Registrados.Should().Equal([(seguro.Id, TipoAccesoDocumentoSensible.Apertura)]);
    }

    // ---- Límites

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(LimitesUnionPdf.MaximoFicheros + 1)]
    public async Task Fuera_de_dos_a_veinte_PDF_no_se_une_nada(int cuantos)
    {
        var entradas = Enumerable.Range(0, cuantos).Select(i => (EntradaUnionPdf)Subido($"f{i}.pdf", $"F{i}")).ToList();

        var resultado = await UnirAsync(entradas);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.NumeroDeFicherosInvalido);
        _unidor.Unidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Veinte_PDF_es_el_maximo_que_se_une()
    {
        var entradas = Enumerable.Range(0, LimitesUnionPdf.MaximoFicheros).Select(i => (EntradaUnionPdf)Subido($"f{i}.pdf", $"F{i}")).ToList();

        (await UnirAsync(entradas)).Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
    }

    [Fact]
    public async Task Un_PDF_subido_de_mas_de_diez_megas_se_rechaza_en_su_posicion()
    {
        var grande = new byte[LimitesArchivoSubido.TamanoMaximoBytes + 1];
        "%PDF-1.4"u8.CopyTo(grande);

        var resultado = await UnirAsync([Subido("a.pdf", "A"), new EntradaUnionSubida(grande, "grande.pdf")]);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.EntradaRechazada);
        resultado.Valor.Posiciones.Should().Equal(1);
        resultado.Valor.CodigoRechazo.Should().Be(ErroresUnirPdf.DemasiadoGrande);
        _unidor.Unidos.Should().BeEmpty();
    }

    [Theory]
    [InlineData("nota.txt", "%PDF-1.4 X")]
    [InlineData("falso.pdf", "esto no es un pdf")]
    public async Task Lo_que_no_es_un_PDF_se_rechaza_por_extension_o_por_firma(string nombre, string contenido)
    {
        var resultado = await UnirAsync([Subido("a.pdf", "A"), new EntradaUnionSubida(Encoding.UTF8.GetBytes(contenido), nombre)]);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.EntradaRechazada);
        resultado.Valor.Posiciones.Should().Equal(1);
        resultado.Valor.CodigoRechazo.Should().Be(ErroresUnirPdf.NoEsPdf);
    }

    [Fact]
    public async Task Si_el_total_pasa_de_cincuenta_megas_no_se_une_nada()
    {
        // Cada PDF cabe en el tope por fichero (10 MB); juntos pasan del total (6 x 9 MB = 54 MB).
        var nueve = new byte[9 * 1024 * 1024];
        "%PDF-1.4 X"u8.CopyTo(nueve);
        var entradas = Enumerable.Range(0, 6).Select(i => (EntradaUnionPdf)new EntradaUnionSubida(nueve, $"f{i}.pdf")).ToList();

        var resultado = await UnirAsync(entradas);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.TotalDemasiadoGrande);
        _unidor.Unidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_las_paginas_suman_mas_de_quinientas_no_se_une_nada()
    {
        var resultado = await UnirAsync([Subido("a.pdf", "A[p=300]"), Subido("b.pdf", "B[p=201]")]);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.TotalDemasiadasPaginas);
        _unidor.Unidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Quinientas_paginas_justas_si_se_unen()
    {
        (await UnirAsync([Subido("a.pdf", "A[p=300]"), Subido("b.pdf", "B[p=200]")])).Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
    }

    [Fact]
    public async Task Un_PDF_con_contrasena_se_rechaza_en_su_posicion_con_su_causa()
    {
        var resultado = await UnirAsync([Subido("a.pdf", "A"), Subido("b.pdf", "B[clave]"), Subido("c.pdf", "C")]);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.EntradaRechazada);
        resultado.Valor.Posiciones.Should().Equal(1);
        resultado.Valor.CodigoRechazo.Should().Be(ErroresUnirPdf.ConContrasena);
    }

    [Fact]
    public async Task Un_PDF_ilegible_se_rechaza_en_su_posicion()
    {
        var resultado = await UnirAsync([Subido("a.pdf", "A[ilegible]"), Subido("b.pdf", "B")]);

        resultado.Valor.Posiciones.Should().Equal(0);
        resultado.Valor.CodigoRechazo.Should().Be(ErroresUnirPdf.Ilegible);
    }

    // ---- Tenant propietario y disponibilidad

    [Fact]
    public async Task Si_el_Tenant_activo_ya_no_es_el_de_la_pantalla_no_se_une_nada()
    {
        _tenantActual = Guid.NewGuid();

        var resultado = await UnirAsync([Subido("a.pdf", "A"), Subido("b.pdf", "B")]);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.TenantCruzado);
        _unidor.Unidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_la_herramienta_disponible_no_se_une_nada()
    {
        _disponible = false;

        (await UnirAsync([Subido("a.pdf", "A"), Subido("b.pdf", "B")])).Error.Codigo.Should().Be(ErroresUnirPdf.NoDisponible);
    }

    // ---- Alcance de cartera

    [Fact]
    public async Task Una_Empresa_gestionada_fuera_de_la_cartera_del_Gestor_CAE_responde_como_inexistente()
    {
        _alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsParaGestion: [_otraEmpresa.Id], trabajadorIdsVisibles: []);

        var resultado = await UnirAsync([Subido("a.pdf", "A"), Subido("b.pdf", "B")]);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.EmpresaNoGestionable);
        _unidor.Unidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_Subcontrata_no_es_una_Empresa_gestionada_de_la_que_unir()
    {
        var subcontrata = Empresa.CrearComoSubcontrata("Pintura Sur S.L.", null, "Gestionada");
        _empresas.ListaEmpresas.Add(subcontrata);

        var resultado = await UnirAsync([Subido("a.pdf", "A"), Subido("b.pdf", "B")], empresaId: subcontrata.Id);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.EmpresaNoGestionable);
    }

    [Fact]
    public async Task Un_Documento_de_otra_Empresa_del_Tenant_no_se_une_ni_se_distingue_de_uno_inexistente()
    {
        var ajeno = Documento.DeEmpresa(_otraEmpresa.Id, Guid.NewGuid(), Hoy.AddDays(-10), VigenciaDocumento.NoCaduca, Guardar("AJENO"));
        _documentos.ListaDocumentos.Add(ajeno);

        var deOtra = await UnirAsync([Subido("a.pdf", "A"), new EntradaUnionDocumento(ajeno.Id)]);
        var inexistente = await UnirAsync([Subido("a.pdf", "A"), new EntradaUnionDocumento(Guid.NewGuid())]);

        deOtra.Valor.Should().BeEquivalentTo(inexistente.Valor);
        deOtra.Valor.Estado.Should().Be(EstadoUnionPdf.EntradaRechazada);
        deOtra.Valor.Posiciones.Should().Equal(1);
        deOtra.Valor.CodigoRechazo.Should().Be(ErroresUnirPdf.DocumentoNoDisponible);
        _unidor.Unidos.Should().BeEmpty();
        _registro.Registrados.Should().BeEmpty("no se abrió nada, así que no hay acceso que registrar");
    }

    [Fact]
    public async Task Un_Documento_de_un_Trabajador_fuera_de_la_cartera_del_Gestor_CAE_no_se_une()
    {
        var reconocimiento = DocumentoDeTrabajador(_ana, "RECONOCIMIENTO");
        var seguro = DocumentoDeEmpresa("SEGURO");
        _alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, empresaIdsParaGestion: [_empresa.Id], empresaIdsVisibles: [_empresa.Id], trabajadorIdsVisibles: []);

        var fuera = await UnirAsync([new EntradaUnionDocumento(seguro.Id), new EntradaUnionDocumento(reconocimiento.Id)]);

        fuera.Valor.Estado.Should().Be(EstadoUnionPdf.EntradaRechazada);
        fuera.Valor.Posiciones.Should().Equal(1);
        fuera.Valor.CodigoRechazo.Should().Be(ErroresUnirPdf.DocumentoNoDisponible);
        _registro.Registrados.Should().Equal([(seguro.Id, TipoAccesoDocumentoSensible.Apertura)],
            "se abrió el Documento anterior; el de fuera de cartera nunca se abrió y no deja registro");
    }

    [Fact]
    public async Task Un_Documento_de_un_Trabajador_dentro_de_la_cartera_si_se_une()
    {
        var reconocimiento = DocumentoDeTrabajador(_ana, "RECONOCIMIENTO");
        _alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, empresaIdsParaGestion: [_empresa.Id], empresaIdsVisibles: [_empresa.Id], trabajadorIdsVisibles: [_ana.Id]);

        var resultado = await UnirAsync([new EntradaUnionDocumento(reconocimiento.Id), Subido("a.pdf", "A")]);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
    }

    // ---- Rol y Sesión Privilegiada

    [Theory]
    [InlineData("Cliente")]
    [InlineData("Lector")]
    [InlineData(null)]
    public async Task Un_rol_que_no_escribe_no_lee_Documentos_para_unirlos(string? rol)
    {
        _rol = rol;
        var seguro = DocumentoDeEmpresa("SEGURO");

        var resultado = await UnirAsync([new EntradaUnionDocumento(seguro.Id), Subido("a.pdf", "A")]);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.RolSinAcceso);
        _registro.Registrados.Should().BeEmpty();
        _unidor.Unidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_rol_que_no_escribe_si_une_PDF_que_sube_el_mismo_porque_no_lee_datos_del_Tenant()
    {
        _rol = "Cliente";

        (await UnirAsync([Subido("a.pdf", "A"), Subido("b.pdf", "B")])).Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
    }

    [Fact]
    public async Task Una_Sesion_Privilegiada_de_plataforma_no_lee_Documentos_para_unirlos()
    {
        _rol = null;
        _sesionPrivilegiada = new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CapacidadPrivilegio.AdminPlataforma, null);
        var seguro = DocumentoDeEmpresa("SEGURO");

        var resultado = await UnirAsync([new EntradaUnionDocumento(seguro.Id), Subido("a.pdf", "A")]);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.SesionPrivilegiadaSinLectura);
        _registro.Registrados.Should().BeEmpty();
    }

    // ---- Firma electrónica

    [Fact]
    public async Task Un_PDF_firmado_sin_confirmar_avisa_con_sus_posiciones_y_no_une_pero_registra_la_apertura_de_lo_abierto()
    {
        var seguro = DocumentoDeEmpresa("SEGURO[firmado]");

        var resultado = await UnirAsync([Subido("a.pdf", "A"), new EntradaUnionDocumento(seguro.Id), Subido("b.pdf", "B[firmado]")]);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.FirmasSinConfirmar);
        resultado.Valor.Posiciones.Should().Equal(1, 2);
        resultado.Valor.Contenido.Should().BeNull("sin confirmar no se entrega nada");
        _unidor.Unidos.Should().BeEmpty();
        _registro.Registrados.Should().Equal([(seguro.Id, TipoAccesoDocumentoSensible.Apertura)],
            "el Documento se abrió para detectar la firma: es una lectura aunque aún no se una nada");
    }

    [Fact]
    public async Task Un_PDF_firmado_confirmado_se_une_y_el_Documento_que_sale_se_registra()
    {
        var seguro = DocumentoDeEmpresa("SEGURO[firmado]");

        var resultado = await UnirAsync([Subido("a.pdf", "A"), new EntradaUnionDocumento(seguro.Id)], confirmaPerderFirmas: true);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
        _unidor.Unidos.Should().Equal("A", "SEGURO[firmado]");
        _registro.Registrados.Should().Equal([(seguro.Id, TipoAccesoDocumentoSensible.Apertura)]);
    }

    // ---- Registro de acceso (DEC-36)

    [Fact]
    public async Task Cada_Documento_distinto_que_entra_se_registra_como_apertura_dentro_de_la_transaccion_y_antes_de_entregar()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");
        var reconocimiento = DocumentoDeTrabajador(_ana, "RECONOCIMIENTO");

        var resultado = await UnirAsync([
            new EntradaUnionDocumento(seguro.Id), Subido("a.pdf", "A"), new EntradaUnionDocumento(reconocimiento.Id)]);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
        _registro.Registrados.Should().Equal([
            (seguro.Id, TipoAccesoDocumentoSensible.Apertura), (reconocimiento.Id, TipoAccesoDocumentoSensible.Apertura)]);
        _registro.ConTransaccionAbierta.Should().Equal([true, true], "las aperturas van en una sola transacción");
        _transaccion.Confirmadas.Should().Be(1);
    }

    [Fact]
    public async Task Sin_constancia_del_acceso_a_un_Documento_no_hay_PDF()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");
        _registro.Falla = true;

        var unir = () => UnirAsync([new EntradaUnionDocumento(seguro.Id), Subido("a.pdf", "A")]);

        await unir.Should().ThrowAsync<AccesoDocumentoSensibleNoRegistradoException>("el registro falla en cerrado (DEC-36)");
    }

    [Fact]
    public async Task Unir_solo_PDF_subidos_no_registra_accesos_ni_abre_transaccion()
    {
        await UnirAsync([Subido("a.pdf", "A"), Subido("b.pdf", "B")]);

        _registro.Registrados.Should().BeEmpty();
        _transaccion.Ejecutadas.Should().Be(0);
    }

    // ---- Almacenamiento

    [Fact]
    public async Task Si_el_archivo_de_un_Documento_ya_no_esta_en_el_almacen_se_rechaza_ese_PDF_sin_registrar()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");
        _almacenamiento.ExcepcionAlAbrir = new FileNotFoundException();

        var resultado = await UnirAsync([Subido("a.pdf", "A"), new EntradaUnionDocumento(seguro.Id)]);

        resultado.Valor.Posiciones.Should().Equal(1);
        resultado.Valor.CodigoRechazo.Should().Be(ErroresUnirPdf.ArchivoNoDisponible);
        _registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_hueco_en_el_presupuesto_de_memoria_se_pide_reintentar_y_no_se_registra_nada()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");
        _almacenamiento.ExcepcionAlAbrir = new PresupuestoMemoriaAgotadoException();

        var resultado = await UnirAsync([Subido("a.pdf", "A"), new EntradaUnionDocumento(seguro.Id)]);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.SinEspacio);
        _registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_archivo_almacenado_que_no_es_un_PDF_se_rechaza()
    {
        var corrupto = Documento.DeEmpresa(_empresa.Id, Guid.NewGuid(), Hoy.AddDays(-10), VigenciaDocumento.NoCaduca, Guardar("no es pdf", conCabecera: false));
        _documentos.ListaDocumentos.Add(corrupto);

        var resultado = await UnirAsync([Subido("a.pdf", "A"), new EntradaUnionDocumento(corrupto.Id)]);

        resultado.Valor.CodigoRechazo.Should().Be(ErroresUnirPdf.NoEsPdf);
        _registro.Registrados.Should().Equal([(corrupto.Id, TipoAccesoDocumentoSensible.Apertura)],
            "el archivo se abrió para comprobarlo: es una lectura aunque no salga PDF");
    }

    // ---- Montaje

    private Task<Result<ResultadoUnionPdf>> UnirAsync(
        IReadOnlyList<EntradaUnionPdf> entradas, bool confirmaPerderFirmas = false, Guid? empresaId = null) =>
        Handler().Handle(new UnirPdfQuery(entradas, _tenantId, empresaId ?? _empresa.Id, confirmaPerderFirmas), CancellationToken.None);

    private UnirPdfQueryHandler Handler() => new(
        new Disponibilidad(_disponible), new TenantActualFijo(_tenantActual), _empresas, _documentos, _trabajadores, _proyectos, _alcance,
        new CurrentUserServiceFalso(rol: _rol), new SesionPrivilegiadaFija(_sesionPrivilegiada), _almacenamiento, _unidor, _registro, _transaccion);

    private static EntradaUnionSubida Subido(string nombre, string marca) => new(Pdf(marca), nombre);

    private static byte[] Pdf(string marca) => Encoding.UTF8.GetBytes("%PDF-1.4 " + marca);

    private string Guardar(string contenido, bool conCabecera = true) =>
        _almacenamiento.GuardarAsync(new MemoryStream(conCabecera ? Pdf(contenido) : Encoding.UTF8.GetBytes(contenido)), "doc.pdf").GetAwaiter().GetResult();

    private Documento DocumentoDeEmpresa(string marca)
    {
        var documento = Documento.DeEmpresa(_empresa.Id, Guid.NewGuid(), Hoy.AddDays(-10), VigenciaDocumento.NoCaduca, Guardar(marca));
        _documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    private Documento DocumentoDeTrabajador(Trabajador trabajador, string marca)
    {
        var documento = Documento.DeTrabajador(trabajador.Id, Guid.NewGuid(), Hoy.AddDays(-10), VigenciaDocumento.NoCaduca, Guardar(marca));
        _documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    private sealed class Disponibilidad(bool disponible) : IDisponibilidadHerramientasPdf
    {
        public ValueTask<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(disponible);
    }

    /// <summary>
    /// Unidor que lee marcas del contenido de prueba: <c>[p=N]</c> páginas (1 por defecto), <c>[firmado]</c>, <c>[clave]</c> y
    /// <c>[ilegible]</c>. Anota las marcas de lo unido en el orden recibido, sin las de páginas. Que PdfSharp una de verdad lo
    /// prueba <c>UnionSobrePdfRealTests</c>.
    /// </summary>
    private sealed class UnidorPdfFalso : IUnidorPdf
    {
        public List<string> Unidos { get; } = [];

        public Result<InfoPdfParaUnir> Inspeccionar(byte[] contenidoPdf)
        {
            var texto = Texto(contenidoPdf);
            if (texto.Contains("[clave]")) return Result.Fallo<InfoPdfParaUnir>(Error.Crear(ErroresUnirPdf.ConContrasena, "con contraseña"));
            if (texto.Contains("[ilegible]")) return Result.Fallo<InfoPdfParaUnir>(Error.Crear(ErroresUnirPdf.Ilegible, "ilegible"));
            return Result.Exito(new InfoPdfParaUnir(Paginas(texto), texto.Contains("[firmado]")));
        }

        public Result<PdfUnido> Unir(IReadOnlyList<byte[]> pdfs)
        {
            var paginas = 0;
            foreach (var pdf in pdfs)
            {
                var texto = Texto(pdf);
                Unidos.Add(System.Text.RegularExpressions.Regex.Replace(texto, @"\[p=\d+\]", ""));
                paginas += Paginas(texto);
            }

            return Result.Exito(new PdfUnido(Encoding.UTF8.GetBytes("%PDF-1.4 unido"), paginas));
        }

        private static string Texto(byte[] contenido) => Encoding.UTF8.GetString(contenido)["%PDF-1.4 ".Length..];

        private static int Paginas(string texto)
        {
            var marca = System.Text.RegularExpressions.Regex.Match(texto, @"\[p=(\d+)\]");
            return marca.Success ? int.Parse(marca.Groups[1].Value) : 1;
        }
    }

    /// <summary>Anota qué Documentos se registran y si la transacción seguía abierta; con <see cref="Falla"/> lanza en cerrado.</summary>
    private sealed class RegistroQueAnota(TransaccionConEstado transaccion) : IRegistroAccesoDocumentoSensibleService
    {
        public List<(Guid DocumentoId, TipoAccesoDocumentoSensible Tipo)> Registrados { get; } = [];
        public List<bool> ConTransaccionAbierta { get; } = [];
        public bool Falla { get; set; }

        public Task RegistrarSiSensibleAsync(Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
        {
            ConTransaccionAbierta.Add(transaccion.Abierta);
            if (Falla) throw new AccesoDocumentoSensibleNoRegistradoException(documentoId);
            Registrados.Add((documentoId, tipoAcceso));
            return Task.CompletedTask;
        }

        public Task RegistrarSiSensibleAsync(Guid recursoId, Guid tipoDocumentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TransaccionConEstado : ITransaccionDeComando
    {
        public bool Abierta { get; private set; }
        public int Ejecutadas { get; private set; }
        public int Confirmadas { get; private set; }

        public async Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
        {
            Ejecutadas++;
            Abierta = true;
            try
            {
                var resultado = await operacion(cancellationToken);
                if (!resultado.EsFallido) Confirmadas++;
                return resultado;
            }
            finally
            {
                Abierta = false;
            }
        }
    }

    private sealed class SesionPrivilegiadaFija(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
    }
}
