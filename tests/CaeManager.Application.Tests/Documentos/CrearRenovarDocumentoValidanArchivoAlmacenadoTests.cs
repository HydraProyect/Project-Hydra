using CaeManager.Application.Common.Archivos;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// OWASP ASVS 4.0.3 V12.1/V12.2: «hasta que haya antivirus, todo fichero que entre como Documento se valida por firma».
/// Crear y Renovar reciben una clave de archivo ya almacenado y no pueden fiarse de quien la mandó: comprueban la firma
/// del contenido referenciado con el mismo validador que la subida directa. Cada rechazo lleva su control positivo (el
/// mismo escenario con un PDF real pasa) para que el rojo no se deba a otra cosa. Contextos falsos, sin Postgres.
/// </summary>
public class CrearRenovarDocumentoValidanArchivoAlmacenadoTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();
    private static readonly byte[] PdfValido = "%PDF-1.4\n%%EOF"u8.ToArray();
    private static readonly byte[] Ejecutable = [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];

    [Fact]
    public async Task Crear_con_un_archivo_cuyo_contenido_es_un_PDF_real_se_acepta()
    {
        var (handler, documentos, almacen, trabajador, tipo) = PrepararCrear();
        var clave = await almacen.GuardarAsync(new MemoryStream(PdfValido), "documento.pdf");

        var resultado = await handler.Handle(NuevoCrear(trabajador.Id, tipo.Id, clave), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("control: un PDF con firma %PDF- pasa");
        documentos.Documentos.Should().ContainSingle().Which.ArchivoUrl.Should().Be(clave);
    }

    [Theory]
    [InlineData("documento.pdf")] // ejecutable disfrazado de PDF
    [InlineData("documento.png")] // ejecutable disfrazado de imagen
    [InlineData("documento.docx")] // ejecutable disfrazado de Word
    public async Task Crear_con_una_firma_falsa_se_rechaza_con_el_error_localizado_y_no_deja_Documento(string nombre)
    {
        var (handler, documentos, almacen, trabajador, tipo) = PrepararCrear();
        var clave = await almacen.GuardarAsync(new MemoryStream(Ejecutable), nombre);

        var resultado = await handler.Handle(NuevoCrear(trabajador.Id, tipo.Id, clave), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.ArchivoNoValido");
        resultado.Error.Mensaje.Should().Be(ValidadorFirmaArchivo.MensajeDocumentoNoValido);
        documentos.Documentos.Should().BeEmpty("un archivo cuyo contenido no es lo que dice su extensión no entra como Documento");
    }

    [Theory]
    [InlineData("programa.exe")]
    [InlineData("paquete.zip")]
    [InlineData("pagina.html")]
    public async Task Crear_con_una_extension_que_no_es_de_documento_se_rechaza_aunque_la_firma_sea_valida(string nombre)
    {
        var (handler, documentos, almacen, trabajador, tipo) = PrepararCrear();
        var clave = await almacen.GuardarAsync(new MemoryStream(PdfValido), nombre);

        var resultado = await handler.Handle(NuevoCrear(trabajador.Id, tipo.Id, clave), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.ArchivoNoValido");
        documentos.Documentos.Should().BeEmpty();
    }

    [Fact]
    public async Task Crear_con_una_clave_que_no_existe_en_el_almacenamiento_se_rechaza_sin_lanzar()
    {
        var (handler, documentos, _, trabajador, tipo) = PrepararCrear();

        var resultado = await handler.Handle(NuevoCrear(trabajador.Id, tipo.Id, "falso-99-inexistente.pdf"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.ArchivoNoValido");
        documentos.Documentos.Should().BeEmpty();
    }

    [Fact]
    public async Task Renovar_con_un_archivo_nuevo_cuyo_contenido_es_un_PDF_real_se_acepta_y_sustituye_al_vigente()
    {
        var (handler, documentos, almacen, documento) = PrepararRenovar();
        var clave = await almacen.GuardarAsync(new MemoryStream(PdfValido), "documento.pdf");

        var resultado = await handler.Handle(NuevoRenovar(documento.Id, clave), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("control: un PDF con firma %PDF- pasa");
        documentos.Documentos.Should().HaveCount(2, "el vigente pasa al historial y nace el nuevo");
    }

    [Fact]
    public async Task Renovar_con_una_firma_falsa_se_rechaza_con_el_error_localizado_y_no_toca_el_Documento()
    {
        var (handler, documentos, almacen, documento) = PrepararRenovar();
        var versionAntes = documento.Version;
        var clave = await almacen.GuardarAsync(new MemoryStream(Ejecutable), "documento.pdf");

        var resultado = await handler.Handle(NuevoRenovar(documento.Id, clave), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.ArchivoNoValido");
        resultado.Error.Mensaje.Should().Be(ValidadorFirmaArchivo.MensajeDocumentoNoValido);
        documentos.Documentos.Should().ContainSingle("no nace ningún Documento nuevo");
        documento.Version.Should().Be(versionAntes, "el vigente no se toca");
    }

    [Fact]
    public async Task Renovar_con_una_clave_inexistente_se_rechaza_sin_lanzar()
    {
        var (handler, documentos, _, documento) = PrepararRenovar();

        var resultado = await handler.Handle(NuevoRenovar(documento.Id, "falso-99-inexistente.pdf"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Documento.ArchivoNoValido");
        documentos.Documentos.Should().ContainSingle();
    }

    [Fact]
    public async Task Renovar_sin_archivo_nuevo_corrige_fechas_sin_leer_el_almacenamiento()
    {
        var (handler, _, _, documento) = PrepararRenovar();

        var resultado = await handler.Handle(NuevoRenovar(documento.Id, null), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("sin archivo nuevo no hay contenido que validar");
        resultado.Valor.Should().Be(documento.Id, "corregir fechas no cambia el Id");
    }

    private static CrearDocumentoCommand NuevoCrear(Guid trabajadorId, Guid tipoId, string clave) =>
        new(trabajadorId, null, null, null, null, tipoId, Hoy, null, clave, null);

    private static RenovarDocumentoCommand NuevoRenovar(Guid documentoId, string? clave) =>
        new(documentoId, Hoy, null, clave, null, NoCaduca: true);

    private static (CrearDocumentoCommandHandler Handler, DocumentoRepositorioFalso Documentos, FileStorageServiceFalso Almacen,
        Domain.Trabajadores.Trabajador Trabajador, TipoDocumento Tipo) PrepararCrear()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        var documentos = new DocumentoRepositorioFalso();
        var almacen = new FileStorageServiceFalso();
        var handler = new CrearDocumentoCommandHandler(
            documentos, mundo.TiposDocumentoContexto, mundo.TrabajadoresContexto, new VehiculosVacios(),
            new ProyectosQueryContextFalso(), new EmpresasQueryContextFalso(), new UnitOfWorkFalso(),
            new CaminosDeAltaAcreditacionesTests.ColaAnalisisSinUso(), new CurrentUserServiceFalso(), mundo.Servicio(),
            new PublisherFalso(), new AlcanceDatosServiceFalso(), new VerificadorArchivoAlmacenado(almacen));
        return (handler, documentos, almacen, trabajador, tipo);
    }

    private static (RenovarDocumentoCommandHandler Handler, DocumentoRepositorioFalso Documentos, FileStorageServiceFalso Almacen,
        Documento Documento) PrepararRenovar()
    {
        var mundo = new MundoAcreditaciones();
        var tipo = mundo.Tipo(RequisitoDocumental.No, AmbitoAplicacion.Empresa, "Certificado");
        var documento = mundo.DocumentoDeEmpresa(tipo);
        var documentos = new DocumentoRepositorioFalso();
        documentos.Documentos.Add(documento);
        var almacen = new FileStorageServiceFalso();
        var handler = new RenovarDocumentoCommandHandler(
            documentos, mundo.TiposDocumentoContexto, new AlcanceDatosServiceFalso(), new ProyectosQueryContextFalso(),
            new CaminosDeAltaAcreditacionesTests.ColaAnalisisSinUso(), new CurrentUserServiceFalso(), mundo.Acreditaciones,
            mundo.Servicio(), new PublisherFalso(), new UnitOfWorkFalso(), new VerificadorArchivoAlmacenado(almacen));
        return (handler, documentos, almacen, documento);
    }

    private sealed class VehiculosVacios : IVehiculosQueryContext
    {
        public IQueryable<Vehiculo> Vehiculos => new Integraciones.TestAsyncQueryable<Vehiculo>(Array.Empty<Vehiculo>().AsQueryable());
    }
}
