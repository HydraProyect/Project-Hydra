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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    private IFileStorageService? _otroAlmacen;
    private Guid _tenantActual;

    public UnirPdfQueryHandlerTests()
    {
        _tenantActual = _tenantId;
        _registro = new RegistroQueAnota(_transaccion, documentoId => _documentos.ListaDocumentos.FirstOrDefault(d => d.Id == documentoId)?.TipoDocumentoId);
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
    public async Task Si_el_unidor_lanza_con_un_Documento_ya_abierto_la_excepcion_sigue_y_la_Apertura_queda()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");

        var unir = () => UnirAsync([new EntradaUnionDocumento(seguro.Id), Subido("b.pdf", "B[explota]")]);

        await unir.Should().ThrowAsync<InvalidOperationException>("la excepción original no se enmascara");
        _registro.Registrados.Should().Equal([(seguro.Id, TipoAccesoDocumentoSensible.Apertura)],
            "el Documento se abrió antes de la excepción: es una lectura aunque no salga nada");
    }

    [Fact]
    public async Task Sin_turno_para_el_proceso_aparte_pide_reintentar_y_el_Documento_ya_abierto_deja_su_Apertura()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");

        var resultado = await UnirAsync([new EntradaUnionDocumento(seguro.Id), Subido("b.pdf", "B[sin-turno]")]);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.SinEspacio, "sin turno es un transitorio, no un PDF ilegible");
        _registro.Registrados.Should().Equal([(seguro.Id, TipoAccesoDocumentoSensible.Apertura)]);
        _unidor.Unidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_el_total_pasa_de_cincuenta_megas_el_Documento_ya_abierto_deja_su_Apertura()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");
        var nueve = new byte[9 * 1024 * 1024];
        "%PDF-1.4 X"u8.CopyTo(nueve);
        var entradas = new List<EntradaUnionPdf> { new EntradaUnionDocumento(seguro.Id) };
        entradas.AddRange(Enumerable.Range(0, 6).Select(i => (EntradaUnionPdf)new EntradaUnionSubida(nueve, $"f{i}.pdf")));

        var resultado = await UnirAsync(entradas);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.TotalDemasiadoGrande);
        _registro.Registrados.Should().Equal([(seguro.Id, TipoAccesoDocumentoSensible.Apertura)],
            "el Documento se abrió antes de superar el tope: es una lectura aunque el resultado sea un fallo");
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

        var entradas = new List<EntradaUnionPdf> { Subido("a.pdf", "A"), new EntradaUnionDocumento(seguro.Id) };
        var aviso = await UnirAsync(entradas);
        aviso.Valor.Estado.Should().Be(EstadoUnionPdf.FirmasSinConfirmar);

        var resultado = await UnirAsync(entradas, aviso.Valor.HuellaFirmas);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
        _unidor.Unidos.Should().Equal("A", "SEGURO[firmado]");
        _registro.Registrados.Should().Equal([(seguro.Id, TipoAccesoDocumentoSensible.Apertura), (seguro.Id, TipoAccesoDocumentoSensible.Apertura)],
            "el aviso y la unión confirmada leen el Documento cada uno: dos aperturas");
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
        PresupuestoLibre().Should().BeTrue("el PDF unido que no sale tampoco retiene su reserva");
    }

    [Fact]
    public async Task Si_la_transaccion_del_registro_no_confirma_no_hay_PDF_ni_queda_su_reserva()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");
        _transaccion.Fallo = Error.Crear("Transaccion.NoConfirmada", "no confirmó");

        var resultado = await UnirAsync([new EntradaUnionDocumento(seguro.Id), Subido("a.pdf", "A")]);

        resultado.EsFallido.Should().BeTrue("sin constancia del acceso no sale el PDF (DEC-36)");
        resultado.Error.Codigo.Should().Be("Transaccion.NoConfirmada");
        PresupuestoLibre().Should().BeTrue("el PDF unido que no sale tampoco retiene su reserva");
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

    // ---- Confirmación de firmas ligada a lo mostrado

    [Fact]
    public async Task La_confirmacion_de_firmas_no_vale_si_cambia_el_contenido_de_un_PDF_firmado()
    {
        var aviso = await UnirAsync([Subido("a.pdf", "A[firmado]"), Subido("b.pdf", "B")]);
        aviso.Valor.HuellaFirmas.Should().NotBeNullOrEmpty();

        var otro = await UnirAsync([Subido("a.pdf", "A2[firmado]"), Subido("b.pdf", "B")], aviso.Valor.HuellaFirmas);

        otro.Valor.Estado.Should().Be(EstadoUnionPdf.FirmasSinConfirmar, "la firma que se aceptó no es la de este PDF");
        otro.Valor.HuellaFirmas.Should().NotBe(aviso.Valor.HuellaFirmas);
        _unidor.Unidos.Should().BeEmpty();
    }

    [Fact]
    public async Task La_confirmacion_de_firmas_no_vale_si_aparece_otro_PDF_firmado_o_cambia_el_orden()
    {
        var aviso = await UnirAsync([Subido("a.pdf", "A[firmado]"), Subido("b.pdf", "B")]);

        (await UnirAsync([Subido("a.pdf", "A[firmado]"), Subido("b.pdf", "B[firmado]")], aviso.Valor.HuellaFirmas))
            .Valor.Estado.Should().Be(EstadoUnionPdf.FirmasSinConfirmar);
        (await UnirAsync([Subido("b.pdf", "B"), Subido("a.pdf", "A[firmado]")], aviso.Valor.HuellaFirmas))
            .Valor.Estado.Should().Be(EstadoUnionPdf.FirmasSinConfirmar);
        _unidor.Unidos.Should().BeEmpty();
    }

    [Fact]
    public async Task La_confirmacion_de_firmas_no_vale_si_el_archivo_vigente_del_Documento_es_otro()
    {
        var seguro = DocumentoDeEmpresa("SEGURO[firmado]");
        var entradas = new List<EntradaUnionPdf> { Subido("a.pdf", "A"), new EntradaUnionDocumento(seguro.Id) };
        var aviso = await UnirAsync(entradas);

        // Entre el aviso y la confirmación, el Documento pasa a tener otro archivo vigente, también firmado.
        _otroAlmacen = new AlmacenConContenidoFijo(Pdf("SEGURO-NUEVO[firmado]"));
        var resultado = await UnirAsync(entradas, aviso.Valor.HuellaFirmas);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.FirmasSinConfirmar);
        _unidor.Unidos.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_PDF_firmados_la_huella_no_hace_falta()
    {
        (await UnirAsync([Subido("a.pdf", "A"), Subido("b.pdf", "B")])).Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
    }

    // ---- Lectura acotada de Documentos

    [Fact]
    public async Task Un_Documento_que_declara_mas_del_total_no_se_lee_ni_se_registra()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");
        var almacen = new AlmacenQueMideLaLectura(LimitesUnionPdf.TamanoMaximoTotalBytes + 1, seekable: true);
        _otroAlmacen = almacen;

        var resultado = await UnirAsync([new EntradaUnionDocumento(seguro.Id), Subido("a.pdf", "A")]);

        resultado.Valor.CodigoRechazo.Should().Be(ErroresUnirPdf.DemasiadoGrande);
        almacen.MaximosPedidos.Should().Equal(LimitesUnionPdf.TamanoMaximoTotalBytes);
        almacen.BytesLeidos.Should().Be(0, "el tamaño almacenado decide antes de leer nada");
        _registro.Registrados.Should().BeEmpty("no se llegó a leer el Documento");
    }

    [Fact]
    public async Task El_tope_que_se_pide_al_abrir_es_lo_que_queda_del_total()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");
        var almacen = new AlmacenQueMideLaLectura(1024, seekable: true);
        _otroAlmacen = almacen;
        var subido = Subido("a.pdf", "A");

        await UnirAsync([subido, new EntradaUnionDocumento(seguro.Id)]);

        almacen.MaximosPedidos.Should().Equal(LimitesUnionPdf.TamanoMaximoTotalBytes - subido.Contenido.Length);
    }

    [Fact]
    public async Task Un_flujo_sin_longitud_que_entrega_de_mas_se_corta_al_pasar_del_tope()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");
        var almacen = new AlmacenQueMideLaLectura(3L * LimitesUnionPdf.TamanoMaximoTotalBytes, seekable: false);
        _otroAlmacen = almacen;

        var resultado = await UnirAsync([new EntradaUnionDocumento(seguro.Id), Subido("a.pdf", "A")]);

        resultado.Valor.CodigoRechazo.Should().Be(ErroresUnirPdf.DemasiadoGrande);
        almacen.BytesLeidos.Should().BeLessThanOrEqualTo(LimitesUnionPdf.TamanoMaximoTotalBytes + 81_920,
            "la copia se para en cuanto pasa del tope, no lee el archivo entero");
    }

    // ---- Presupuesto de memoria

    [Theory]
    [InlineData("unido")]
    [InlineData("rechazado por ilegible")]
    [InlineData("firma sin confirmar")]
    [InlineData("excepcion del unidor")]
    [InlineData("documento que no es PDF")]
    public async Task Al_terminar_sea_cual_sea_el_desenlace_no_queda_nada_reservado_del_presupuesto(string desenlace)
    {
        var seguro = DocumentoDeEmpresa(desenlace == "firma sin confirmar" ? "SEGURO[firmado][estructura=1000000]" : "SEGURO[estructura=1000000]");
        var corrupto = Documento.DeEmpresa(_empresa.Id, Guid.NewGuid(), Hoy.AddDays(-10), VigenciaDocumento.NoCaduca, Guardar("no es pdf", conCabecera: false));
        _documentos.ListaDocumentos.Add(corrupto);
        EntradaUnionPdf segunda = desenlace switch
        {
            "rechazado por ilegible" => Subido("b.pdf", "B[ilegible]"),
            "excepcion del unidor" => Subido("b.pdf", "B[explota]"),
            "documento que no es PDF" => new EntradaUnionDocumento(corrupto.Id),
            _ => Subido("b.pdf", "B"),
        };

        var llamada = async () => await UnirAsync([new EntradaUnionDocumento(seguro.Id), segunda]);
        if (desenlace == "excepcion del unidor")
            await llamada.Should().ThrowAsync<InvalidOperationException>();
        else
            // Lo que se entrega lo desecha quien lo recibe; eso es lo único que retiene el PDF unido (ver el test siguiente).
            (await llamada()).Valor.Dispose();

        PresupuestoLibre().Should().BeTrue("las reservas de lectura, de copias y de estructura se devuelven siempre");
    }

    private const long Factor = LimitesUnionPdf.FactorMemoriaUnion;

    [Fact]
    public async Task Si_el_proceso_de_una_inspeccion_no_consta_terminado_solo_se_retiene_lo_de_su_entrada_y_la_excepcion_sigue()
    {
        // Una escritura del proceso aparte puede seguir referenciando el PDF que recibió: lo reservado para él se queda
        // retenido. Lo del Documento anterior, cuyo proceso de inspección sí terminó, se devuelve.
        var seguro = DocumentoDeEmpresa("SEGURO[estructura=1000000]");
        var subido = Subido("b.pdf", "B[retenida]");

        var llamada = async () => await UnirAsync([new EntradaUnionDocumento(seguro.Id), subido]);

        await llamada.Should().ThrowAsync<EntradaPdfRetenidaException>("la pantalla tiene que retener también lo suyo");
        _presupuesto.Reservado.Should().Be((Factor - 1) * subido.Contenido.Length,
            "solo queda la copia de lo subido que recibió el proceso sin terminar; la lectura y la estructura del Documento se devuelven");
    }

    [Fact]
    public async Task Si_queda_retenida_la_inspeccion_de_un_Documento_recien_leido_se_retiene_su_lectura()
    {
        // La lectura del Documento cubre el array que recibió el proceso sin terminar; lo de la subida anterior se devuelve.
        var subido = Subido("a.pdf", "A[estructura=1000000]");
        var retenido = DocumentoDeEmpresa("X[retenida]");

        var llamada = async () => await UnirAsync([subido, new EntradaUnionDocumento(retenido.Id)]);

        await llamada.Should().ThrowAsync<EntradaPdfRetenidaException>();
        _presupuesto.Reservado.Should().Be(Factor * Pdf("X[retenida]").Length,
            "solo la lectura del Documento; ni la copia ni la estructura de la subida, cuya inspección terminó");
    }

    [Fact]
    public async Task Varias_inspecciones_ya_terminadas_devuelven_lo_suyo_aunque_la_ultima_quede_retenida()
    {
        // Con 512 MiB compartidos y hasta 32 MiB de estructura por PDF, retenerlo todo inmovilizaría casi todo el presupuesto.
        var a = DocumentoDeEmpresa("A[estructura=30000000]");
        var b = DocumentoDeEmpresa("B[estructura=30000000]");
        var c = Subido("c.pdf", "C[estructura=30000000]");
        var retenido = Subido("d.pdf", "D[retenida]");

        var llamada = async () => await UnirAsync([new EntradaUnionDocumento(a.Id), new EntradaUnionDocumento(b.Id), c, retenido]);

        await llamada.Should().ThrowAsync<EntradaPdfRetenidaException>();
        _presupuesto.Reservado.Should().Be((Factor - 1) * retenido.Contenido.Length);
    }

    [Fact]
    public async Task Un_Documento_repetido_cuyo_proceso_queda_retenido_retiene_tambien_la_lectura_de_su_array()
    {
        // El repetido es el mismo array que la primera lectura: si el proceso lo sigue referenciando, esa lectura tampoco vuelve.
        var seguro = DocumentoDeEmpresa("SEGURO[estructura=1000000]");
        var longitud = Pdf("SEGURO[estructura=1000000]").Length;
        _unidor.RetenerEnInspeccion = 3;

        var llamada = async () => await UnirAsync(
            [new EntradaUnionDocumento(seguro.Id), Subido("b.pdf", "B[estructura=1000000]"), new EntradaUnionDocumento(seguro.Id)]);

        await llamada.Should().ThrowAsync<EntradaPdfRetenidaException>();
        _presupuesto.Reservado.Should().Be(Factor * longitud + (Factor - 1) * longitud,
            "la lectura del Documento y la copia del repetido; ni las estructuras ni la copia de lo subido");
    }

    [Fact]
    public async Task Si_el_proceso_de_la_union_no_consta_terminado_se_retiene_todo_lo_que_recibio()
    {
        // El proceso que une recibe todos los PDF y vuelve a abrir su estructura: todo lo reservado le corresponde.
        var seguro = DocumentoDeEmpresa("SEGURO[estructura=1000000]");
        var subido = Subido("b.pdf", "B[estructura=2000]");
        _unidor.AlUnir = () => throw new EntradaPdfRetenidaException();

        var llamada = async () => await UnirAsync([new EntradaUnionDocumento(seguro.Id), subido]);

        await llamada.Should().ThrowAsync<EntradaPdfRetenidaException>();
        _presupuesto.Reservado.Should().Be(
            Factor * Pdf("SEGURO[estructura=1000000]").Length + 1000000 + (Factor - 1) * subido.Contenido.Length + 2000);
    }

    [Fact]
    public async Task Si_ademas_falla_el_registro_de_aperturas_la_entrada_retenida_no_se_pierde_y_el_fallo_queda_registrado()
    {
        // Si el fallo del registro sustituyera a la retención, la pantalla devolvería su reserva de lo subido. Pero los
        // Documentos ya se leyeron y pueden quedar sin constancia: ese fallo tiene que verlo quien supervisa.
        var seguro = DocumentoDeEmpresa("SEGURO");
        _registro.Falla = true;

        var llamada = async () => await UnirAsync([new EntradaUnionDocumento(seguro.Id), Subido("b.pdf", "B[retenida]")]);

        await llamada.Should().ThrowAsync<EntradaPdfRetenidaException>();
        PresupuestoLibre().Should().BeFalse();
        _logger.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Critical)
            .Which.Should().Match<(LogLevel Nivel, string Mensaje, Exception? Excepcion)>(e =>
                e.Excepcion is AccesoDocumentoSensibleNoRegistradoException && e.Mensaje.Contains(seguro.Id.ToString()));
    }

    [Fact]
    public async Task Si_tambien_falla_el_proveedor_de_registro_la_entrada_retenida_sigue_siendo_la_excepcion()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");
        _registro.Falla = true;
        _logger.Lanza = true;

        var llamada = async () => await UnirAsync([new EntradaUnionDocumento(seguro.Id), Subido("b.pdf", "B[retenida]")]);

        await llamada.Should().ThrowExactlyAsync<EntradaPdfRetenidaException>("la pantalla tiene que retener lo suyo aunque el registro falle");
        PresupuestoLibre().Should().BeFalse();
    }

    [Fact]
    public async Task El_PDF_unido_retiene_su_reserva_hasta_que_quien_lo_entrega_lo_desecha_y_la_devuelve_una_sola_vez()
    {
        var seguro = DocumentoDeEmpresa("SEGURO[estructura=1000000]");

        var resultado = await UnirAsync([new EntradaUnionDocumento(seguro.Id), Subido("b.pdf", "B")]);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
        _presupuesto.Reservado.Should().Be(resultado.Valor.Contenido!.LongLength,
            "mientras se descarga, el PDF unido sigue en memoria y cuenta; lo demás de la unión ya se devolvió");

        resultado.Valor.Dispose();
        resultado.Valor.Dispose();
        _presupuesto.Reservado.Should().Be(0);
        using var otra = await _presupuesto.ReservarAsync(1);
        resultado.Valor.Dispose();
        _presupuesto.Reservado.Should().Be(1, "desecharlo otra vez no devuelve lo que es de otra reserva");
    }

    [Fact]
    public async Task Sin_hueco_para_retener_el_PDF_unido_pide_reintentar_y_no_queda_nada_reservado()
    {
        _unidor.AlUnir = () =>
        {
            // Lo que otros retienen deja hueco para unir, pero no para quedarse además con el PDF unido.
            var libre = _presupuesto.Maximo - _presupuesto.Reservado;
            _presupuesto.TryReservar(libre, out _ocupado).Should().BeTrue();
        };

        var resultado = await UnirAsync([Subido("a.pdf", "A"), Subido("b.pdf", "B")]);
        _ocupado!.Dispose();

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.SinEspacio);
        PresupuestoLibre().Should().BeTrue();
    }

    private ReservaMemoriaDocumentos? _ocupado;

    [Fact]
    public async Task Mientras_se_une_el_presupuesto_retiene_la_estructura_que_el_unidor_midio()
    {
        // Con la mitad del presupuesto como estructura y contenidos diminutos, sin la reserva de estructura casi todo seguiría libre.
        var estructura = MaximoPresupuesto / 2;
        var libreMientrasSeUne = new List<bool>();
        _unidor.AlUnir = () =>
        {
            if (_presupuesto.TryReservar(MaximoPresupuesto - estructura + 1, out var demasiado))
            {
                demasiado.Dispose();
                libreMientrasSeUne.Add(true);
            }
            else
                libreMientrasSeUne.Add(false);
        };

        var resultado = await UnirAsync([Subido("a.pdf", $"A[estructura={estructura}]"), Subido("b.pdf", "B")]);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
        libreMientrasSeUne.Should().Equal(false);
    }

    [Fact]
    public async Task Una_estructura_que_no_cabe_en_el_presupuesto_pide_reintentar_y_no_une_nada()
    {
        var resultado = await UnirAsync([Subido("a.pdf", $"A[estructura={MaximoPresupuesto + 1}]"), Subido("b.pdf", "B")]);

        resultado.Error.Codigo.Should().Be(ErroresUnirPdf.SinEspacio);
        _unidor.Unidos.Should().BeEmpty();
        PresupuestoLibre().Should().BeTrue();
    }

    // ---- Cancelación

    [Fact]
    public async Task Si_la_peticion_se_cancela_tras_leer_el_Documento_la_Apertura_queda_registrada()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");
        using var cancelacion = new CancellationTokenSource();
        _unidor.AlUnir = cancelacion.Cancel;

        var resultado = await UnirAsync([new EntradaUnionDocumento(seguro.Id), Subido("a.pdf", "A")], cancellationToken: cancelacion.Token);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
        _registro.Registrados.Should().Equal([(seguro.Id, TipoAccesoDocumentoSensible.Apertura)],
            "el Documento ya se leyó: la desconexión no puede dejarlo sin constancia");
        _transaccion.Confirmadas.Should().Be(1);
    }

    // ---- DEC-36: nunca registrar de más

    [Fact]
    public async Task Un_Documento_sin_datos_personales_se_une_pero_no_deja_registro_y_el_sensible_si()
    {
        var seguro = DocumentoDeEmpresa("SEGURO");
        var reconocimiento = DocumentoDeTrabajador(_ana, "RECONOCIMIENTO");
        _registro.TiposSinDatosPersonales.Add(seguro.TipoDocumentoId);

        var resultado = await UnirAsync([new EntradaUnionDocumento(seguro.Id), new EntradaUnionDocumento(reconocimiento.Id)]);

        resultado.Valor.Estado.Should().Be(EstadoUnionPdf.Unido);
        _unidor.Unidos.Should().Equal("SEGURO", "RECONOCIMIENTO");
        _registro.Registrados.Should().Equal([(reconocimiento.Id, TipoAccesoDocumentoSensible.Apertura)],
            "DEC-36: un Documento SinDatosPersonales no genera fila; el sensible sí");
    }

    /// <summary>Devuelve siempre el mismo contenido: el archivo vigente de cualquier Documento ha pasado a ser este.</summary>
    private sealed class AlmacenConContenidoFijo(byte[] contenido) : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenidoNuevo, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new MemoryStream(contenido, writable: false));
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>
    /// Un archivo de <c>longitud</c> bytes que se genera al leerlo (nunca está entero en memoria) y cuenta lo que se lee. Con
    /// longitud declarada, <see cref="AbrirHastaAsync"/> rechaza antes de entregar el flujo, como el almacenamiento real; sin
    /// ella, solo la copia acotada puede pararlo.
    /// </summary>
    private sealed class AlmacenQueMideLaLectura(long longitud, bool seekable) : IFileStorageService
    {
        public List<long> MaximosPedidos { get; } = [];
        public long BytesLeidos { get; private set; }

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(new FlujoGenerado(this, longitud, seekable));
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<Stream> AbrirHastaAsync(string identificador, long maximoBytes, CancellationToken cancellationToken = default)
        {
            MaximosPedidos.Add(maximoBytes);
            if (seekable && longitud > maximoBytes)
                throw new ArchivoAlmacenadoDemasiadoGrandeException();
            return AbrirAsync(identificador, cancellationToken);
        }

        private sealed class FlujoGenerado(AlmacenQueMideLaLectura almacen, long longitud, bool seekable) : Stream
        {
            private long _posicion;
            public override bool CanRead => true;
            public override bool CanSeek => seekable;
            public override bool CanWrite => false;
            public override long Length => seekable ? longitud : throw new NotSupportedException();
            public override long Position { get => _posicion; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                var leidos = (int)Math.Min(count, longitud - _posicion);
                if (leidos <= 0) return 0;
                if (_posicion == 0 && leidos >= 9) "%PDF-1.4 "u8.CopyTo(buffer.AsSpan(offset));
                _posicion += leidos;
                almacen.BytesLeidos += leidos;
                return leidos;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    // ---- Montaje

    private Task<Result<ResultadoUnionPdf>> UnirAsync(
        IReadOnlyList<EntradaUnionPdf> entradas, string? firmasConfirmadas = null, Guid? empresaId = null,
        CancellationToken cancellationToken = default) =>
        Handler().Handle(new UnirPdfQuery(entradas, _tenantId, empresaId ?? _empresa.Id, firmasConfirmadas), cancellationToken);

    private UnirPdfQueryHandler Handler() => new(
        new Disponibilidad(_disponible), new TenantActualFijo(_tenantActual), _empresas, _documentos, _trabajadores, _proyectos, _alcance,
        new CurrentUserServiceFalso(rol: _rol), new SesionPrivilegiadaFija(_sesionPrivilegiada), _otroAlmacen ?? _almacenamiento,
        _presupuesto, _unidor, _registro, _transaccion, _logger);

    private readonly LoggerQueAnota _logger = new();

    private sealed class LoggerQueAnota : ILogger<UnirPdfQueryHandler>
    {
        public List<(LogLevel Nivel, string Mensaje, Exception? Excepcion)> Entradas { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public bool Lanza { get; set; }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Lanza) throw new AggregateException(new InvalidOperationException("proveedor de registro roto"));
            Entradas.Add((logLevel, formatter(state, exception), exception));
        }
    }

    private const long MaximoPresupuesto = 2L * 1024 * 1024 * 1024;

    private readonly PresupuestoMemoriaDocumentos _presupuesto = new(
        Options.Create(new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = MaximoPresupuesto, EsperaMaxima = TimeSpan.FromMilliseconds(100) }),
        TimeProvider.System);

    /// <summary>Verdadero si no queda nada reservado: el presupuesto entero cabe ahora mismo (lo que se reserva se devuelve al terminar).</summary>
    private bool PresupuestoLibre()
    {
        if (!_presupuesto.TryReservar(_presupuesto.Maximo, out var todo))
            return false;
        todo.Dispose();
        return true;
    }

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
        public Action? AlUnir { get; set; }

        /// <summary>Si se fija, la inspección número N (desde 1) lanza <see cref="EntradaPdfRetenidaException"/>.</summary>
        public int? RetenerEnInspeccion { get; set; }

        private int _inspecciones;

        public Task<Result<InfoPdfParaUnir>> InspeccionarAsync(byte[] contenidoPdf, CancellationToken cancellationToken)
        {
            if (++_inspecciones == RetenerEnInspeccion) throw new EntradaPdfRetenidaException();
            return Task.FromResult(Inspeccionar(contenidoPdf));
        }

        public Task<Result<PdfUnido>> UnirAsync(IReadOnlyList<byte[]> pdfs, CancellationToken cancellationToken) =>
            Task.FromResult(Unir(pdfs));

        private Result<InfoPdfParaUnir> Inspeccionar(byte[] contenidoPdf)
        {
            var texto = Texto(contenidoPdf);
            if (texto.Contains("[explota]")) throw new InvalidOperationException("fallo inesperado del unidor");
            if (texto.Contains("[sin-turno]")) throw new PresupuestoMemoriaAgotadoException();
            if (texto.Contains("[retenida]")) throw new EntradaPdfRetenidaException();
            if (texto.Contains("[clave]")) return Result.Fallo<InfoPdfParaUnir>(Error.Crear(ErroresUnirPdf.ConContrasena, "con contraseña"));
            if (texto.Contains("[ilegible]")) return Result.Fallo<InfoPdfParaUnir>(Error.Crear(ErroresUnirPdf.Ilegible, "ilegible"));
            var estructura = System.Text.RegularExpressions.Regex.Match(texto, @"\[estructura=(\d+)\]");
            return Result.Exito(new InfoPdfParaUnir(
                Paginas(texto), texto.Contains("[firmado]"), estructura.Success ? long.Parse(estructura.Groups[1].Value) : 0));
        }

        private Result<PdfUnido> Unir(IReadOnlyList<byte[]> pdfs)
        {
            AlUnir?.Invoke();
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

    /// <summary>
    /// Anota qué Documentos se registran y si la transacción seguía abierta; con <see cref="Falla"/> lanza en cerrado. Como el
    /// servicio real (DEC-36, «nunca registrar de más»), no registra un Documento cuyo tipo está en <see cref="TiposSinDatosPersonales"/>.
    /// </summary>
    private sealed class RegistroQueAnota(TransaccionConEstado transaccion, Func<Guid, Guid?> tipoDe) : IRegistroAccesoDocumentoSensibleService
    {
        public List<(Guid DocumentoId, TipoAccesoDocumentoSensible Tipo)> Registrados { get; } = [];
        public List<bool> ConTransaccionAbierta { get; } = [];
        public bool Falla { get; set; }
        public HashSet<Guid> TiposSinDatosPersonales { get; } = [];

        public Task RegistrarSiSensibleAsync(Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
        {
            ConTransaccionAbierta.Add(transaccion.Abierta);
            if (Falla) throw new AccesoDocumentoSensibleNoRegistradoException(documentoId);
            if (tipoDe(documentoId) is { } tipo && TiposSinDatosPersonales.Contains(tipo)) return Task.CompletedTask;
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
        /// <summary>Si no es <c>null</c>, la transacción no confirma y devuelve este fallo sin lanzar.</summary>
        public Error? Fallo { get; set; }

        public async Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
        {
            // Como la transacción real, que usa el token al empezar y al confirmar.
            cancellationToken.ThrowIfCancellationRequested();
            Ejecutadas++;
            Abierta = true;
            try
            {
                var resultado = await operacion(cancellationToken);
                if (Fallo is not null) return Result.Fallo(Fallo);
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
