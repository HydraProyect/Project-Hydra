using CaeManager.Application.Asignaciones.Commands.CrearAsignacion;
using CaeManager.Application.Asignaciones.Commands.CrearAsignaciones;
using CaeManager.Application.Asignaciones.Commands.ReactivarAsignacion;
using CaeManager.Application.Centros.Commands.CrearCanalGestion;
using CaeManager.Application.Centros.Commands.EstablecerDocumentacionRequeridaCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Importacion;
using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Application.Tests.Asignaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Importacion;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// Cada camino de alta que pone un Documento ante un Centro crea la acreditación
/// de plataforma a través de la regla única (P0-7), en el mismo guardado que el
/// alta. Una prueba por camino: si alguien retira la llamada de uno solo, su
/// prueba se pone en rojo. La plantilla se prueba en
/// GenerarDocumentoIndividualCommandHandlerTests, que tiene su propio entorno.
/// </summary>
public class CaminosDeAltaAcreditacionesTests
{
    private static readonly DateOnly Hoy = new(2026, 1, 15);

    [Fact]
    public async Task Crear_documento_a_mano_lo_acredita_ante_los_centros_del_trabajador_que_exigen_su_tipo()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        var centroQueLoExige = mundo.Centro("Planta Norte");
        var acceso = mundo.AccesoPlataforma(centroQueLoExige);
        mundo.Asignacion(trabajador, centroQueLoExige);
        var centroQueLoExcluye = mundo.Centro("Planta Sur");
        mundo.AccesoPlataforma(centroQueLoExcluye);
        mundo.Requisito(tipo, centroQueLoExcluye, incluido: false);
        mundo.Asignacion(trabajador, centroQueLoExcluye);
        var documentos = new DocumentoRepositorioFalso();
        var handler = new CrearDocumentoCommandHandler(
            documentos, mundo.TiposDocumentoContexto, mundo.TrabajadoresContexto, new VehiculosVacios(),
            new ProyectosQueryContextFalso(), new EmpresasQueryContextFalso(), new UnitOfWorkFalso(),
            new ColaAnalisisSinUso(), new CurrentUserServiceFalso(), mundo.Servicio(), new PublisherFalso(),
            new AlcanceDatosServiceFalso(), new VerificadorArchivoAlmacenadoPermisivo());

        var resultado = await handler.Handle(
            new CrearDocumentoCommand(trabajador.Id, null, null, null, null, tipo.Id, Hoy, null, null, null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mundo.Agregadas.Should().BeEquivalentTo([(resultado.Valor, acceso.Id)]);
    }

    [Fact]
    public async Task Importar_un_documento_lo_acredita_ante_el_centro_donde_el_trabajador_ya_esta_asignado()
    {
        var escenario = EscenarioConCentroQueExigeElTipo();
        escenario.ConAsignacionActivaExistente();

        var resultado = await escenario.EjecutarAsync(EscenarioImportacion.Plan(documentos:
        [
            new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, Hoy, YaExiste: false),
        ]));

        resultado.DocumentosCreados.Should().Be(1);
        var documento = escenario.DocumentoRepositorio.Documentos.Should().ContainSingle().Subject;
        escenario.AcreditacionRepositorio.Acreditaciones.Select(a => (a.DocumentoId, a.CanalGestionDocumentalId))
            .Should().BeEquivalentTo([(documento.Id, escenario.CentrosContexto.ListaCanalesGestionDocumental[0].Id)]);
    }

    [Fact]
    public async Task Importar_una_asignacion_acredita_los_documentos_que_el_trabajador_ya_tenia()
    {
        var escenario = EscenarioConCentroQueExigeElTipo();
        escenario.ConDocumentoExistente();

        var resultado = await escenario.EjecutarAsync(EscenarioImportacion.Plan(asignaciones:
        [
            new AsignacionImportadaDto(EscenarioImportacion.DniConocido, EscenarioImportacion.CentroConocido, YaExiste: false),
        ]));

        resultado.AsignacionesCreadas.Should().Be(1);
        escenario.AcreditacionRepositorio.Acreditaciones.Select(a => (a.DocumentoId, a.CanalGestionDocumentalId))
            .Should().BeEquivalentTo([(
                escenario.DocumentosContexto.ListaDocumentos[0].Id,
                escenario.CentrosContexto.ListaCanalesGestionDocumental[0].Id)]);
    }

    [Fact]
    public async Task Importar_ante_un_centro_que_no_exige_el_tipo_no_crea_acreditaciones()
    {
        var escenario = new EscenarioImportacion()
            .ConEmpresaExistente().ConTrabajadorExistente().ConCentroExistente().ConTipoDocumentoExistente()
            .ConAsignacionActivaExistente();
        escenario.CentrosContexto.ListaCanalesGestionDocumental.Add(CanalGestionDocumental.DePlataforma(
            escenario.CentroExistente!.Id, "Gestión general", Guid.NewGuid(), null, null, null));

        var resultado = await escenario.EjecutarAsync(EscenarioImportacion.Plan(documentos:
        [
            new DocumentoImportadoDto(EscenarioImportacion.DniConocido, EscenarioImportacion.TipoDocumentoConocido, Hoy, YaExiste: false),
        ]));

        resultado.DocumentosCreados.Should().Be(1);
        escenario.AcreditacionRepositorio.Acreditaciones.Should().BeEmpty(
            "el tipo sembrado no es obligatorio por defecto y el Centro no tiene fila que lo incluya");
    }

    [Fact]
    public async Task Crear_una_asignacion_acredita_los_documentos_del_trabajador_y_de_su_empresa_que_el_centro_exige()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        var acceso = mundo.AccesoPlataforma(centro);
        var delTrabajador = mundo.DocumentoDe(trabajador, mundo.Tipo(RequisitoDocumental.Si));
        var deLaEmpresa = mundo.DocumentoDeEmpresa(mundo.Tipo(RequisitoDocumental.Si, AmbitoAplicacion.Empresa, "Seguro RC"));
        mundo.DocumentoDe(trabajador, mundo.Tipo(RequisitoDocumental.No, nombre: "Carné de carretillero"));
        var handler = new CrearAsignacionCommandHandler(
            new AsignacionRepositorioFalso(), new AutoridadTotal(), mundo.Servicio(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new CrearAsignacionCommand(trabajador.Id, centro.Id, Hoy), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mundo.Agregadas.Should().BeEquivalentTo([(delTrabajador.Id, acceso.Id), (deLaEmpresa.Id, acceso.Id)],
            "el Documento cuyo tipo el Centro no exige no se acredita");
    }

    /// <summary>
    /// FS-13: reabrir una asignación es volver a darla de alta. Lo que el Centro
    /// exige y se subió mientras estaba cerrada nace pendiente de acreditar.
    /// </summary>
    [Fact]
    public async Task Reabrir_una_asignacion_acredita_los_documentos_que_el_centro_exige()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        var acceso = mundo.AccesoPlataforma(centro);
        var delTrabajador = mundo.DocumentoDe(trabajador, mundo.Tipo(RequisitoDocumental.Si));
        var repositorio = new AsignacionRepositorioFalso();
        var cerrada = new Domain.Asignaciones.Asignacion(trabajador.Id, centro.Id, Hoy.AddDays(-30));
        cerrada.DarDeBaja(Hoy);
        repositorio.Asignaciones.Add(cerrada);
        var handler = new ReactivarAsignacionCommandHandler(repositorio, new AutoridadTotal(), mundo.Servicio(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new ReactivarAsignacionCommand(cerrada.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mundo.Agregadas.Should().Contain((delTrabajador.Id, acceso.Id));
    }

    [Fact]
    public async Task Crear_asignaciones_en_lote_acredita_ante_cada_centro_solo_lo_que_ese_centro_exige()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        var documento = mundo.DocumentoDe(trabajador, tipo);
        var centroQueLoExige = mundo.Centro("Planta Norte");
        var acceso = mundo.AccesoPlataforma(centroQueLoExige);
        mundo.Requisito(tipo, centroQueLoExige, incluido: true);
        var centroQueNoLoExige = mundo.Centro("Planta Sur");
        mundo.AccesoPlataforma(centroQueNoLoExige);
        var handler = new CrearAsignacionesCommandHandler(
            new AsignacionRepositorioFalso(), mundo.AsignacionesContexto, new AutoridadTotal(), mundo.Servicio(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new CrearAsignacionesCommand([trabajador.Id], [centroQueLoExige.Id, centroQueNoLoExige.Id], Hoy), CancellationToken.None);

        resultado.Valor.Creadas.Should().Be(2);
        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)]);
    }

    [Fact]
    public async Task Crear_un_acceso_de_plataforma_acredita_ante_el_solo_los_documentos_que_su_centro_exige()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        // Acceso previo sin sus acreditaciones: el alta de otro acceso no lo repara.
        mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro);
        var exigido = mundo.DocumentoDe(trabajador, mundo.Tipo(RequisitoDocumental.Si));
        mundo.DocumentoDe(trabajador, mundo.Tipo(RequisitoDocumental.No, nombre: "Carné de carretillero"));
        var proveedores = new ProveedoresPlataformaCaeQueryContextFalso();
        var proveedor = new ProveedorPlataformaCae("ctaima", "CTAIMA");
        proveedores.ListaProveedores.Add(proveedor);
        var handler = new CrearCanalGestionCommandHandler(
            new CanalesGestionEnMemoria(), new AlcanceDatosServiceFalso(), proveedores, mundo.CentrosContexto, mundo.Servicio(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new CrearCanalGestionCommand(centro.Id, TipoCanalGestion.Plataforma, "Acceso subcontratas", proveedor.Id,
                null, null, null, null, null, null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mundo.Agregadas.Should().BeEquivalentTo([(exigido.Id, resultado.Valor)]);
    }

    private static byte[] DocxMinimo()
    {
        using var memoria = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memoria, System.IO.Compression.ZipArchiveMode.Create, true))
            foreach (var nombre in new[] { "[Content_Types].xml", "word/document.xml" })
            {
                using var flujo = zip.CreateEntry(nombre).Open();
                flujo.Write("<a/>"u8);
            }
        return memoria.ToArray();
    }

    // ASVS V12.1/V12.2 (seguimiento de #1812): el servidor valida la clave de archivo y el nombre original, también
    // para Incluido = false, y solo admite PDF o Word.
    [Theory]
    [InlineData("inexistente", false, true, "archivo.pdf", false)]
    [InlineData("pdf-malo", false, true, "archivo.pdf", false)]
    [InlineData("pdf-malo", false, false, "archivo.pdf", false)]
    [InlineData("pdf-bueno", false, true, "archivo.pdf", true)]
    [InlineData("pdf-bueno", false, false, "archivo.pdf", true)]
    [InlineData("pdf-bueno", false, true, "factura.exe", false)]
    [InlineData("pdf-bueno", false, true, "informe.docx", false)]
    [InlineData("pdf-bueno", false, true, null, false)]
    [InlineData("pdf-bueno", false, true, "ARCHIVO.PDF", true)]
    [InlineData("png-bueno", false, true, "foto.png", false)]
    [InlineData("docx-bueno", false, true, "informe.docx", true)]
    [InlineData("docx-sin-estructura", false, true, "informe.docx", false)]
    [InlineData("pdf-malo", true, true, "archivo.pdf", true)]
    [InlineData("pdf-malo", true, true, "archivo.exe", false)]
    [InlineData("pdf-malo", true, true, null, false)]
    public async Task Requisito_de_centro_valida_la_clave_y_el_contenido_antes_de_guardar(
        string caso, bool claveYaGuardada, bool incluido, string? nombreOriginal, bool valido)
    {
        var mundo = new MundoAcreditaciones();
        var centro = mundo.Centro();
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        var tenantId = Guid.NewGuid();
        AsignarTenant(centro, tenantId);
        var almacen = new FileStorageServiceFalso { TenantId = tenantId };
        var (contenido, extension) = caso switch
        {
            "pdf-bueno" => ("%PDF-1.7"u8.ToArray(), "pdf"),
            "png-bueno" => (new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }, "png"),
            "docx-bueno" => (DocxMinimo(), "docx"),
            "docx-sin-estructura" => (new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 }, "docx"),
            _ => ("MZ"u8.ToArray(), "pdf")
        };
        var clave = caso == "inexistente" ? $"{tenantId:N}/inexistente.pdf" : await almacen.GuardarAsync(new MemoryStream(contenido), $"archivo.{extension}");
        var filas = new TipoDocumentoCentroRepositorioFalso();
        // Una clave que la fila ya tenía (anterior a esta validación) no se vuelve a exigir al editar otros campos.
        if (claveYaGuardada)
            filas.Agregar(new TipoDocumentoCentro(tipo.Id, centro.Id, true, null, false, clave, "archivo.pdf", null));
        var unit = new UnitOfWorkFalso();
        var handler = new EstablecerDocumentacionRequeridaCentroCommandHandler(filas, mundo.CentrosContexto,
            mundo.TiposDocumentoContexto, mundo.Servicio(), new AlcanceDatosServiceFalso(), unit,
            new CaeManager.Application.Common.Archivos.VerificadorArchivoAlmacenado(almacen));

        var resultado = await handler.Handle(new EstablecerDocumentacionRequeridaCentroCommand(
            centro.Id, tipo.Id, incluido, null, false, clave, nombreOriginal, 5), CancellationToken.None);

        resultado.EsExitoso.Should().Be(valido);
        unit.VecesGuardado.Should().Be(valido ? 1 : 0);
        if (!valido) resultado.Error.Codigo.Should().Be("DocumentacionRequeridaCentro.ArchivoNoValido");
    }
    [Fact]
    public async Task Requisito_de_centro_con_fila_existente_valida_la_clave_nueva()
    {
        var mundo = new MundoAcreditaciones();
        var centro = mundo.Centro();
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        var tenantId = Guid.NewGuid();
        AsignarTenant(centro, tenantId);
        var almacen = new FileStorageServiceFalso { TenantId = tenantId };
        var claveAnterior = await almacen.GuardarAsync(new MemoryStream("%PDF-1.7"u8.ToArray()), "anterior.pdf");
        var claveNueva = await almacen.GuardarAsync(new MemoryStream("MZ"u8.ToArray()), "nueva.pdf");
        var filas = new TipoDocumentoCentroRepositorioFalso();
        filas.Agregar(new TipoDocumentoCentro(tipo.Id, centro.Id, true, null, false, claveAnterior, "anterior.pdf", null));
        var unit = new UnitOfWorkFalso();
        var handler = new EstablecerDocumentacionRequeridaCentroCommandHandler(filas, mundo.CentrosContexto,
            mundo.TiposDocumentoContexto, mundo.Servicio(), new AlcanceDatosServiceFalso(), unit,
            new CaeManager.Application.Common.Archivos.VerificadorArchivoAlmacenado(almacen));

        var resultado = await handler.Handle(new EstablecerDocumentacionRequeridaCentroCommand(
            centro.Id, tipo.Id, true, null, false, claveNueva, "nueva.pdf", null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        unit.VecesGuardado.Should().Be(0);
        filas.Relaciones.Single().ArchivoUrl.Should().Be(claveAnterior);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task Requisito_de_centro_solo_admite_clave_nueva_del_tenant_propietario(
        bool clavePropia, bool claveSinCambios, bool admitida)
    {
        var mundo = new MundoAcreditaciones();
        var centro = mundo.Centro();
        var tenantPropietario = Guid.NewGuid();
        AsignarTenant(centro, tenantPropietario);
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        var almacen = new FileStorageServiceFalso { TenantId = clavePropia ? tenantPropietario : Guid.NewGuid() };
        var clave = await almacen.GuardarAsync(new MemoryStream("%PDF-1.7"u8.ToArray()), "archivo.pdf");
        var filas = new TipoDocumentoCentroRepositorioFalso();
        if (claveSinCambios)
            filas.Agregar(new TipoDocumentoCentro(tipo.Id, centro.Id, true, null, false, clave, "archivo.pdf", null));
        var unit = new UnitOfWorkFalso();
        var handler = new EstablecerDocumentacionRequeridaCentroCommandHandler(filas, mundo.CentrosContexto,
            mundo.TiposDocumentoContexto, mundo.Servicio(), new AlcanceDatosServiceFalso(), unit,
            new CaeManager.Application.Common.Archivos.VerificadorArchivoAlmacenado(almacen));

        var resultado = await handler.Handle(new EstablecerDocumentacionRequeridaCentroCommand(
            centro.Id, tipo.Id, true, null, false, clave, "archivo.pdf", 5), CancellationToken.None);

        resultado.EsExitoso.Should().Be(admitida);
        unit.VecesGuardado.Should().Be(admitida ? 1 : 0);
        if (!admitida) resultado.Error.Codigo.Should().Be("DocumentacionRequeridaCentro.ArchivoNoValido");
        else filas.Relaciones.Single().ArchivoUrl.Should().Be(clave);
    }

    // El mismo Error para todas: quien prueba claves no distingue una que existe en otro Tenant de una que no existe.
    // «propia-con-segmentos» existe en el almacén y es un PDF válido: solo la rechaza la forma de la clave.
    [Theory]
    [InlineData("ajena-existente")]
    [InlineData("ajena-inexistente")]
    [InlineData("propia-inexistente")]
    [InlineData("propia-con-segmentos")]
    [InlineData("sin-carpeta")]
    public async Task Requisito_de_centro_rechaza_con_el_mismo_error_toda_clave_fuera_de_la_carpeta_del_tenant(string caso)
    {
        var mundo = new MundoAcreditaciones();
        var centro = mundo.Centro();
        var tenantPropietario = Guid.NewGuid();
        AsignarTenant(centro, tenantPropietario);
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        var tenantAjeno = Guid.NewGuid();
        var almacen = new FileStorageServiceFalso { TenantId = tenantAjeno };
        var claveAjena = await almacen.GuardarAsync(new MemoryStream("%PDF-1.7"u8.ToArray()), "archivo.pdf");
        almacen.TenantId = tenantPropietario;
        var claveConSegmentos = await almacen.GuardarAsync(new MemoryStream("%PDF-1.7"u8.ToArray()), "../archivo.pdf");
        var clave = caso switch
        {
            "ajena-existente" => claveAjena,
            "ajena-inexistente" => $"{tenantAjeno:N}/archivo.pdf",
            "propia-inexistente" => $"{tenantPropietario:N}/archivo.pdf",
            "propia-con-segmentos" => claveConSegmentos,
            _ => "archivo.pdf"
        };
        var filas = new TipoDocumentoCentroRepositorioFalso();
        var unit = new UnitOfWorkFalso();
        var handler = new EstablecerDocumentacionRequeridaCentroCommandHandler(filas, mundo.CentrosContexto,
            mundo.TiposDocumentoContexto, mundo.Servicio(), new AlcanceDatosServiceFalso(), unit,
            new CaeManager.Application.Common.Archivos.VerificadorArchivoAlmacenado(almacen));

        var resultado = await handler.Handle(new EstablecerDocumentacionRequeridaCentroCommand(
            centro.Id, tipo.Id, true, null, false, clave, "archivo.pdf", null), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DocumentacionRequeridaCentro.ArchivoNoValido");
        resultado.Error.Mensaje.Should().Be(CaeManager.Application.Common.Archivos.ValidadorFirmaArchivo.MensajeRequisitoNoValido);
        unit.VecesGuardado.Should().Be(0);
        filas.Relaciones.Should().BeEmpty();
    }

    [Theory]
    [InlineData("11111111111111111111111111111111/archivo.pdf", true)]
    [InlineData("11111111111111111111111111111111/", false)]
    [InlineData("11111111111111111111111111111111/sub/archivo.pdf", false)]
    [InlineData("11111111111111111111111111111111/..archivo.pdf", false)]
    [InlineData("22222222222222222222222222222222/archivo.pdf", false)]
    [InlineData("11111111111111111111111111111111archivo.pdf", false)]
    [InlineData("archivo.pdf", false)]
    public void Una_clave_es_del_tenant_solo_si_es_un_archivo_directo_de_su_carpeta(string clave, bool esDelTenant) =>
        CaeManager.Application.ContratosAcceso.EvidenciasInbound.OriginalesInbound
            .EsDelTenant(clave, Guid.Parse("11111111111111111111111111111111"))
            .Should().Be(esDelTenant);

    private static void AsignarTenant(CaeManager.Domain.Common.EntidadConTenant entidad, Guid tenantId) =>
        typeof(CaeManager.Domain.Common.EntidadConTenant).GetProperty(nameof(entidad.TenantId))!.SetValue(entidad, tenantId);

    [Fact]
    public async Task Que_un_centro_pase_a_exigir_un_tipo_acredita_los_documentos_de_ese_tipo_de_quienes_trabajan_alli()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        var acceso = mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro);
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        var documento = mundo.DocumentoDe(trabajador, tipo);
        mundo.DocumentoDe(trabajador, mundo.Tipo(RequisitoDocumental.Si, nombre: "Reconocimiento médico"));
        var handler = new EstablecerDocumentacionRequeridaCentroCommandHandler(
            new TipoDocumentoCentroRepositorioFalso(), mundo.CentrosContexto, mundo.TiposDocumentoContexto,
            mundo.Servicio(), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso(), new Common.VerificadorArchivoAlmacenadoPermisivo());

        var resultado = await handler.Handle(
            new EstablecerDocumentacionRequeridaCentroCommand(centro.Id, tipo.Id, Incluido: true, null, false, null, null, ToleranciaDias: null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)],
            "solo entra en juego el tipo que el Centro acaba de exigir");
    }

    [Fact]
    public async Task Que_un_centro_excluya_un_tipo_no_crea_acreditaciones()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro);
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        mundo.DocumentoDe(trabajador, tipo);
        var handler = new EstablecerDocumentacionRequeridaCentroCommandHandler(
            new TipoDocumentoCentroRepositorioFalso(), mundo.CentrosContexto, mundo.TiposDocumentoContexto,
            mundo.Servicio(), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso(), new Common.VerificadorArchivoAlmacenadoPermisivo());

        var resultado = await handler.Handle(
            new EstablecerDocumentacionRequeridaCentroCommand(centro.Id, tipo.Id, Incluido: false, null, false, null, null, ToleranciaDias: null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mundo.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Marcar_un_centro_en_el_tipo_de_documento_acredita_los_documentos_de_ese_tipo_de_quienes_trabajan_alli()
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        var acceso = mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro);
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        var documento = mundo.DocumentoDe(trabajador, tipo);
        var tipos = new TipoDocumentoRepositorioFalso();
        tipos.Agregar(tipo);
        var handler = new EditarTipoDocumentoCommandHandler(
            tipos, new TipoDocumentoCentroRepositorioFalso(), mundo.CentrosContexto, mundo.Servicio(), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new EditarTipoDocumentoCommand(
                tipo.Id, tipo.Nombre, 12, true, 1, RequisitoDocumental.No, NaturalezaJuridica.RequisitoCliente,
                null, null, null, null, null, [centro.Id]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        mundo.Agregadas.Should().BeEquivalentTo([(documento.Id, acceso.Id)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Un_tipo_archivado_no_se_exige_en_Centros_nuevos_ni_de_serie_desde_su_edicion(bool deSerie)
    {
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var centro = mundo.Centro();
        mundo.AccesoPlataforma(centro);
        mundo.Asignacion(trabajador, centro);
        var tipo = mundo.Tipo(RequisitoDocumental.No);
        mundo.DocumentoDe(trabajador, tipo);
        tipo.Archivar(null);
        var tipos = new TipoDocumentoRepositorioFalso();
        tipos.Agregar(tipo);
        var filas = new TipoDocumentoCentroRepositorioFalso();
        var handler = new EditarTipoDocumentoCommandHandler(
            tipos, filas, mundo.CentrosContexto, mundo.Servicio(), new AlcanceDatosServiceFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new EditarTipoDocumentoCommand(
                tipo.Id, tipo.Nombre, 12, true, 1, deSerie ? RequisitoDocumental.Si : RequisitoDocumental.No, NaturalezaJuridica.RequisitoCliente,
                null, null, null, null, null, deSerie ? [] : [centro.Id]),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeFalse("un tipo archivado no se pide: se pide su sustituto");
        resultado.Error.Codigo.Should().Be("TipoDocumento.Archivado");
        mundo.Agregadas.Should().BeEmpty();
    }

    /// <summary>
    /// Trabajador, Centro con un acceso de plataforma y un tipo que ese Centro
    /// exige de forma explícita (el tipo sembrado no es obligatorio por defecto).
    /// </summary>
    private static EscenarioImportacion EscenarioConCentroQueExigeElTipo()
    {
        var escenario = new EscenarioImportacion()
            .ConEmpresaExistente().ConTrabajadorExistente().ConCentroExistente().ConTipoDocumentoExistente();
        escenario.CentrosContexto.ListaCanalesGestionDocumental.Add(CanalGestionDocumental.DePlataforma(
            escenario.CentroExistente!.Id, "Gestión general", Guid.NewGuid(), null, null, null));
        escenario.TiposDocumentoContexto.ListaTiposDocumentoCentros.Add(
            new TipoDocumentoCentro(escenario.TipoDocumentoExistente!.Id, escenario.CentroExistente.Id));
        return escenario;
    }

    private sealed class AutoridadTotal : IAutoridadAsignacionesService
    {
        public Task<bool> PuedeModificarAsignacionesDelCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<Guid>> FiltrarCentrosConAutoridadAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(centroIds);

        public Task<bool> PuedeModificarAsignacionesDelTrabajadorAsync(Guid trabajadorId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<Guid>> FiltrarTrabajadoresConAutoridadAsync(IReadOnlyList<Guid> trabajadorIds, CancellationToken cancellationToken = default) =>
            Task.FromResult(trabajadorIds);
    }

    private sealed class CanalesGestionEnMemoria : ICanalGestionDocumentalRepository
    {
        private readonly List<CanalGestionDocumental> canales = [];

        public Task<CanalGestionDocumental?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(canales.FirstOrDefault(c => c.Id == id));

        public Task<IReadOnlyList<CanalGestionDocumental>> ObtenerPorCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CanalGestionDocumental>>(canales.Where(c => c.CentroId == centroId).ToList());

        public void Agregar(CanalGestionDocumental canal) => canales.Add(canal);
    }

    [Fact]
    public async Task Crear_documento_con_verificacion_IA_encola_con_la_version_del_Documento()
    {
        // La versión encolada es la referencia con la que una verificación
        // que llega tarde sabe si el Documento cambió entretanto; sin ella,
        // la verificación de un archivo viejo podría aprobar uno renovado.
        var mundo = new MundoAcreditaciones();
        var trabajador = mundo.Trabajador();
        var tipo = mundo.Tipo(RequisitoDocumental.Si);
        tipo.EstablecerVerificacionIaActiva(true);
        var documentos = new DocumentoRepositorioFalso();
        var cola = new ColaAnalisisEnMemoria();
        var handler = new CrearDocumentoCommandHandler(
            documentos, mundo.TiposDocumentoContexto, mundo.TrabajadoresContexto, new VehiculosVacios(),
            new ProyectosQueryContextFalso(), new EmpresasQueryContextFalso(), new UnitOfWorkFalso(),
            cola, new CurrentUserServiceFalso(), mundo.Servicio(), new PublisherFalso(),
            new AlcanceDatosServiceFalso(), new VerificadorArchivoAlmacenadoPermisivo());

        var resultado = await handler.Handle(
            new CrearDocumentoCommand(trabajador.Id, null, null, null, null, tipo.Id, Hoy, null, "apto.pdf", null),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var documento = documentos.Documentos.Should().ContainSingle().Subject;
        var trabajo = cola.Trabajos.Should().ContainSingle(t => t.Tipo == TipoAnalisisDocumento.VerificacionIa).Subject;
        trabajo.DocumentoId.Should().Be(documento.Id);
        trabajo.VersionDocumentoEncolada.Should().Be(documento.Version);
    }

    private sealed class ColaAnalisisEnMemoria : ITrabajoAnalisisDocumentoRepository
    {
        public List<TrabajoAnalisisDocumento> Trabajos { get; } = [];

        public void Agregar(TrabajoAnalisisDocumento trabajo) => Trabajos.Add(trabajo);

        public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TrabajoAnalisisDocumento?> ReclamarSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(TimeSpan umbral, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> ContarActivosAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class VehiculosVacios : IVehiculosQueryContext
    {
        public IQueryable<Vehiculo> Vehiculos => new Integraciones.TestAsyncQueryable<Vehiculo>(Array.Empty<Vehiculo>().AsQueryable());
    }

    internal sealed class ColaAnalisisSinUso : ITrabajoAnalisisDocumentoRepository
    {
        public void Agregar(TrabajoAnalisisDocumento trabajo) =>
            throw new InvalidOperationException("Sin archivo no se encola ningún análisis.");

        public Task<TrabajoAnalisisDocumento?> ObtenerSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TrabajoAnalisisDocumento?> ReclamarSiguientePendienteAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TrabajoAnalisisDocumento>> ObtenerEstancadosAsync(TimeSpan umbral, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> ContarActivosAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
