using System.IO.Compression;
using CaeManager.Application.Auditoria;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.PaqueteDocumental;
using CaeManager.Application.Empresas.PaqueteDocumental;
using CaeManager.Application.Empresas.Queries.PaqueteDocumentalEmpresa;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// «Preparar paquete» de Empresa 360 (maqueta «Mapa Dia a Dia TALVEG.dc.html» § C): para un Cliente empresarial trae solo
/// lo que ese Cliente exige y está vigente, y dice antes de generar qué falta y qué no pide; «Todo lo vigente» trae todo lo
/// vigente. La puerta es la de las acciones vecinas: Tenant propietario, alcance de gestión sobre la Empresa, rol que
/// descarga y registro fallando en cerrado de cada Documento sensible que sale (DEC-36).
/// </summary>
public class PaqueteDocumentalEmpresaQueriesTests
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly CalculoConParesFijos _calculo = new();
    private readonly FileStorageServiceFalso _almacenamiento = new();
    private readonly TransaccionQueDeshace _transaccion = new();
    private readonly RegistroQueAnota _registro;
    private AlcanceDatosServiceFalso _alcance = new();
    private string? _rol = "GestorCae";
    private SesionPrivilegiadaActiva? _sesionPrivilegiada;
    private long _maximoBytes = PaqueteDocumentalEmpresaService.MaximoBytes;

    private readonly Empresa _empresa = new("Contratista Demo SL", "B12345674");
    private readonly Empresa _cliente = Empresa.CrearComoCliente("Refrielectric SA", "A58818501", false, null, null);
    private readonly Empresa _otroCliente = Empresa.CrearComoCliente("Grupo Arbeko SL", "B95758389", false, null, null);
    private readonly Centro _centroDelCliente;
    private readonly Centro _centroDelOtro;
    private readonly Trabajador _ana;
    private readonly Trabajador _luis;
    private readonly TipoDocumento _reconocimiento = NuevoTipo("Reconocimiento médico", AmbitoAplicacion.Trabajador);
    private readonly TipoDocumento _epi = NuevoTipo("Entrega de EPI", AmbitoAplicacion.Trabajador);
    private readonly TipoDocumento _seguro = NuevoTipo("Seguro RC", AmbitoAplicacion.Empresa);
    private readonly TipoDocumento _iso = NuevoTipo("Certificado ISO", AmbitoAplicacion.Empresa);

    private readonly Documento _epiAna;
    private readonly Documento _epiLuis;
    private readonly Documento _seguroEmpresa;

    public PaqueteDocumentalEmpresaQueriesTests()
    {
        _registro = new RegistroQueAnota(_transaccion);
        _transaccion.AlDeshacer = (antes) => _registro.Registrados.RemoveRange(antes, _registro.Registrados.Count - antes);
        _transaccion.Contar = () => _registro.Registrados.Count;
        _centroDelCliente = new Centro(_cliente.Id, _empresa.Id, "Nave Norte");
        _centroDelOtro = new Centro(_otroCliente.Id, _empresa.Id, "Planta Sur");
        _ana = Trabajador.DeEmpresa(_empresa.Id, "Ana", "Garcia", "12345678Z");
        _luis = Trabajador.DeEmpresa(_empresa.Id, "Luis", "Perez", "77189989B");

        _empresas.ListaEmpresas.AddRange([_empresa, _cliente, _otroCliente]);
        _empresas.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(_empresa.Id, _cliente.Id, DateTime.UtcNow));
        _empresas.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(_empresa.Id, _otroCliente.Id, DateTime.UtcNow));
        _centros.ListaCentros.AddRange([_centroDelCliente, _centroDelOtro]);
        _trabajadores.ListaTrabajadores.AddRange([_ana, _luis]);
        _tipos.ListaTiposDocumento.AddRange([_reconocimiento, _epi, _seguro, _iso]);
        _asignaciones.ListaAsignaciones.Add(new Asignacion(_ana.Id, _centroDelCliente.Id, Hoy.AddDays(-30)));
        _asignaciones.ListaAsignaciones.Add(new Asignacion(_luis.Id, _centroDelOtro.Id, Hoy.AddDays(-30)));

        // El Cliente exige a Ana reconocimiento y EPI y a la Empresa el seguro; el otro Cliente, EPI a Luis y la ISO.
        _calculo.Exigir(_centroDelCliente, _ana, _reconocimiento);
        _calculo.Exigir(_centroDelCliente, _ana, _epi);
        _calculo.ExigirALaEmpresa(_centroDelCliente, _empresa, _seguro);
        _calculo.Exigir(_centroDelOtro, _luis, _epi);
        _calculo.ExigirALaEmpresa(_centroDelOtro, _empresa, _iso);

        DocumentoDeTrabajador(_ana, _reconocimiento, Hoy.AddDays(-400), VigenciaDocumento.VenceEl(Hoy.AddDays(-5)), "reconocimiento-vencido");
        _epiAna = DocumentoDeTrabajador(_ana, _epi, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(300)), "epi-ana");
        _epiLuis = DocumentoDeTrabajador(_luis, _epi, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(300)), "epi-luis");
        _seguroEmpresa = DocumentoDeEmpresa(_seguro, VigenciaDocumento.VenceEl(Hoy.AddDays(100)), "seguro");
        DocumentoDeEmpresa(_iso, VigenciaDocumento.NoCaduca, "iso");
    }

    [Fact]
    public async Task Para_un_Cliente_dice_cuantos_van_cuantos_faltan_o_estan_vencidos_y_cuantos_no_pide()
    {
        var resumen = (await ResumirAsync(_cliente.Id)).Valor;

        resumen.Destinos.Select(d => d.RazonSocial).Should().Equal("Grupo Arbeko SL", "Refrielectric SA");
        resumen.Incluye.Should().Be(2, "el EPI de Ana y el seguro: lo que el Cliente exige y está vigente");
        resumen.Faltan.Should().Be(1, "el reconocimiento de Ana solo tiene una copia vencida");
        resumen.Fuera.Should().Be(2, "el EPI de Luis y la ISO están vigentes pero este Cliente no los pide");
        resumen.ListaFaltan.Should().ContainSingle()
            .Which.Should().Be(new FaltaPaqueteEmpresaDto("Reconocimiento médico", "Ana Garcia", Hoy.AddDays(-5)));
    }

    [Fact]
    public async Task La_descarga_para_un_Cliente_trae_solo_lo_exigido_y_vigente_en_carpetas_por_Trabajador()
    {
        var resultado = await DescargarAsync(_cliente.Id);

        resultado.EsFallido.Should().BeFalse();
        LeerZip(resultado.Valor.Contenido).Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["Empresa/Seguro RC.pdf"] = "seguro",
            ["Trabajadores/Ana Garcia/Entrega de EPI.pdf"] = "epi-ana",
        }, "el vencido nunca viaja y lo que este Cliente no pide no entra");
        resultado.Valor.NombreArchivo.Should().Be($"Contratista-Demo-SL_Refrielectric-SA_{Hoy:yyyy-MM-dd}.zip");
    }

    [Fact]
    public async Task Todo_lo_vigente_trae_todo_lo_vigente_y_nunca_un_vencido()
    {
        var resumen = (await ResumirAsync(null)).Valor;
        var zip = LeerZip((await DescargarAsync(null)).Valor.Contenido);

        resumen.Should().BeEquivalentTo(new { Incluye = 4, Faltan = 0, Fuera = 0 });
        zip.Values.Should().BeEquivalentTo("seguro", "iso", "epi-ana", "epi-luis");
    }

    [Fact]
    public async Task Cada_documento_que_sale_se_registra_como_apertura_antes_de_entregar_el_zip()
    {
        await DescargarAsync(_cliente.Id);

        _registro.Registrados.Should().BeEquivalentTo(new[]
        {
            (_epiAna.Id, TipoAccesoDocumentoSensible.Apertura),
            (_seguroEmpresa.Id, TipoAccesoDocumentoSensible.Apertura),
        });
    }

    [Fact]
    public async Task Sin_constancia_del_acceso_a_un_documento_sensible_no_hay_zip()
    {
        _registro.Falla = true;

        var descargar = () => DescargarAsync(_cliente.Id);

        await descargar.Should().ThrowAsync<AccesoDocumentoSensibleNoRegistradoException>("el registro falla en cerrado (DEC-36)");
    }

    [Fact]
    public async Task Si_falla_el_registro_de_un_documento_que_no_es_el_primero_no_queda_ninguna_apertura_ni_hay_zip()
    {
        _registro.FallaEnLaLlamada = 2;

        var descargar = () => DescargarAsync(_cliente.Id);

        await descargar.Should().ThrowAsync<AccesoDocumentoSensibleNoRegistradoException>("sin constancia no hay zip (DEC-36)");
        _registro.ConTransaccionAbierta.Should().Equal([true, true], "las aperturas del paquete van en una sola transacción");
        _transaccion.Deshechas.Should().Be(1);
        _transaccion.Confirmadas.Should().Be(0);
        _registro.Registrados.Should().BeEmpty("la apertura del primer documento se deshace: el paquete no se entregó");
    }

    [Fact]
    public async Task Las_aperturas_de_un_paquete_entregado_se_confirman_juntas()
    {
        await DescargarAsync(_cliente.Id);

        _registro.ConTransaccionAbierta.Should().Equal([true, true]);
        _transaccion.Ejecutadas.Should().Be(1, "una sola transacción para todas las aperturas del paquete");
        _transaccion.Confirmadas.Should().Be(1);
        _registro.Registrados.Should().HaveCount(2);
    }

    // ---- Puerta: cartera, Tenant propietario, destino y rol

    [Fact]
    public async Task Fuera_de_la_cartera_de_gestion_responde_como_una_Empresa_inexistente_y_no_lee_nada()
    {
        _alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [_empresa.Id], empresaIdsParaGestion: []);

        (await ResumirAsync(_cliente.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.NoEncontrada);
        (await DescargarAsync(_cliente.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.NoEncontrada);
        _registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_Empresa_de_otro_Tenant_que_el_filtro_de_consulta_no_devuelve_responde_como_inexistente()
    {
        // El filtro de consulta del Tenant activo (y RLS) no devuelve las Empresas de otro Tenant: para esta capa, no están.
        // Que el filtro y RLS las oculten se prueba en Integración, no aquí.
        var deOtroTenant = Guid.NewGuid();

        (await Mediador().ResumirAsync(new ObtenerResumenPaqueteEmpresaQuery(deOtroTenant, null))).Error.Should().Be(AutorizacionPaqueteEmpresa.NoEncontrada);
        (await Mediador().DescargarAsync(new DescargarPaqueteEmpresaQuery(deOtroTenant, null))).Error.Should().Be(AutorizacionPaqueteEmpresa.NoEncontrada);
    }

    [Fact]
    public async Task Un_Cliente_empresarial_que_no_es_de_la_Empresa_no_es_destino()
    {
        var ajeno = Empresa.CrearComoCliente("Cliente Ajeno SA", "A08001851", false, null, null);
        _empresas.ListaEmpresas.Add(ajeno);

        (await ResumirAsync(ajeno.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.NoEncontrada);
        (await DescargarAsync(ajeno.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.NoEncontrada);
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Un_rol_que_no_descarga_no_obtiene_los_documentos_sensibles_del_paquete(string? rol)
    {
        _rol = rol;

        (await ResumirAsync(_cliente.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.RolSinDescarga);
        (await DescargarAsync(_cliente.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.RolSinDescarga);
        _registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Trabajador_fuera_del_alcance_de_quien_prepara_el_paquete_no_entra()
    {
        _alcance = new AlcanceDatosServiceFalso(trabajadorIdsVisibles: [_ana.Id]);

        var zip = LeerZip((await DescargarAsync(null)).Valor.Contenido);

        zip.Values.Should().NotContain("epi-luis");
        _registro.Registrados.Select(r => r.DocumentoId).Should().NotContain(_epiLuis.Id);
    }

    [Fact]
    public async Task Sin_nada_vigente_que_entregar_no_se_genera_un_zip_vacio()
    {
        _documentos.ListaDocumentos.RemoveAll(d => d.Id == _epiAna.Id || d.Id == _seguroEmpresa.Id);

        (await DescargarAsync(_cliente.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.SinDocumentos);
    }

    [Fact]
    public async Task Todo_lo_vigente_no_trae_los_Documentos_vigentes_de_otra_Empresa_del_mismo_Tenant_propietario()
    {
        // Otra Empresa del mismo Tenant propietario, con un Documento de Empresa y un Trabajador con el suyo, todo vigente
        // y de tipos que la Empresa del paquete no tiene: si se colara, entraría sin competir con nada.
        var otraEmpresa = new Empresa("Otra Contratista SL", "B76543214");
        var plan = NuevoTipo("Plan de prevención", AmbitoAplicacion.Empresa);
        var formacion = NuevoTipo("Formación PRL", AmbitoAplicacion.Trabajador);
        var marta = Trabajador.DeEmpresa(otraEmpresa.Id, "Marta", "Ruiz", "45678912S");
        _empresas.ListaEmpresas.Add(otraEmpresa);
        _tipos.ListaTiposDocumento.AddRange([plan, formacion]);
        _trabajadores.ListaTrabajadores.Add(marta);
        var planOtra = Documento.DeEmpresa(otraEmpresa.Id, plan.Id, Hoy.AddDays(-10), VigenciaDocumento.NoCaduca, Guardar("plan-otra"));
        var formacionMarta = Documento.DeTrabajador(marta.Id, formacion.Id, Hoy.AddDays(-10), VigenciaDocumento.NoCaduca, Guardar("formacion-marta"));
        _documentos.ListaDocumentos.AddRange([planOtra, formacionMarta]);

        var resumen = (await ResumirAsync(null)).Valor;
        var zip = LeerZip((await DescargarAsync(null)).Valor.Contenido);

        resumen.Incluye.Should().Be(4, "solo cuentan los Documentos de la Empresa del paquete y de sus Trabajadores");
        zip.Values.Should().BeEquivalentTo("seguro", "iso", "epi-ana", "epi-luis");
        _registro.Registrados.Select(r => r.DocumentoId).Should().NotContain(planOtra.Id).And.NotContain(formacionMarta.Id);
    }

    [Fact]
    public async Task Un_archivo_que_no_se_puede_abrir_no_se_omite_en_silencio_va_en_un_indice_dentro_del_zip()
    {
        _documentos.ListaDocumentos.RemoveAll(d => d.Id == _epiAna.Id);
        var epiSinArchivo = Documento.DeTrabajador(_ana.Id, _epi.Id, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(300)), "no-existe.pdf");
        _documentos.ListaDocumentos.Add(epiSinArchivo);

        var resultado = await DescargarAsync(_cliente.Id);

        var zip = LeerZip(resultado.Valor.Contenido);
        zip.Keys.Should().BeEquivalentTo("Empresa/Seguro RC.pdf", PaqueteDocumentalEmpresaService.NombreLeeme);
        zip[PaqueteDocumentalEmpresaService.NombreLeeme].Should().Contain("- Entrega de EPI · Ana Garcia");
        _registro.Registrados.Select(r => r.DocumentoId).Should().NotContain(epiSinArchivo.Id, "lo que no entró no es un acceso a su contenido");
    }

    [Fact]
    public async Task Si_no_se_puede_abrir_ningun_archivo_lo_dice_en_vez_de_decir_que_no_hay_nada_vigente()
    {
        _almacenamiento.ExcepcionAlAbrir = new IOException("almacenamiento caído");

        (await DescargarAsync(_cliente.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.ArchivosNoDisponibles);
        _registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_Sesion_Privilegiada_ve_el_resumen_pero_no_descarga_el_paquete_de_Empresa()
    {
        // Decisión provisional de la coordinadora (2026-11-08), la más restrictiva: el Soporte TALVEG no es Gestor CAE.
        _rol = null;
        _sesionPrivilegiada = new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CapacidadPrivilegio.AdminPlataforma, null);

        var resumen = await ResumirAsync(_cliente.Id);
        var descarga = await DescargarAsync(_cliente.Id);

        resumen.EsFallido.Should().BeFalse();
        resumen.Valor.Should().BeEquivalentTo(new { Incluye = 2, PuedeDescargar = false });
        descarga.Error.Should().Be(AutorizacionPaqueteEmpresa.SesionPrivilegiadaSinDescarga);
        _registro.Registrados.Should().BeEmpty();
    }

    [Fact]
    public async Task Dos_Trabajadores_con_el_mismo_nombre_no_comparten_carpeta_en_el_zip()
    {
        var otraAna = Trabajador.DeEmpresa(_empresa.Id, "ANA", "GARCIA", "11111111H");
        _trabajadores.ListaTrabajadores.Add(otraAna);
        DocumentoDeTrabajador(otraAna, _epi, Hoy.AddDays(-10), VigenciaDocumento.VenceEl(Hoy.AddDays(300)), "epi-otra-ana");
        // El segundo por orden de Id lleva « (2)» detrás de su propio nombre.
        var esperado = _ana.Id.CompareTo(otraAna.Id) < 0
            ? new Dictionary<string, string> { ["Trabajadores/Ana Garcia/Entrega de EPI.pdf"] = "epi-ana", ["Trabajadores/ANA GARCIA (2)/Entrega de EPI.pdf"] = "epi-otra-ana" }
            : new Dictionary<string, string> { ["Trabajadores/ANA GARCIA/Entrega de EPI.pdf"] = "epi-otra-ana", ["Trabajadores/Ana Garcia (2)/Entrega de EPI.pdf"] = "epi-ana" };

        var zip = LeerZip((await DescargarAsync(null)).Valor.Contenido);

        zip.Where(e => e.Key.StartsWith("Trabajadores/Ana", StringComparison.OrdinalIgnoreCase))
            .Should().BeEquivalentTo(esperado, "cada Trabajador tiene su carpeta: el Cliente empresarial no puede mezclar documentos de dos personas");
    }

    [Fact]
    public async Task El_zip_en_memoria_se_corta_al_pasar_su_tope_de_bytes()
    {
        var grupos = new List<IReadOnlyList<ArmadoPaqueteDocumental.Copia>>
        {
            new[] { new ArmadoPaqueteDocumental.Copia(_epiAna.Id, _ana.Id, _epi.Id, _epiAna.ArchivoUrl!) },
            new[] { new ArmadoPaqueteDocumental.Copia(_epiLuis.Id, _luis.Id, _epi.Id, _epiLuis.ArchivoUrl!) },
        };

        var construir = () => ArmadoPaqueteDocumental.ConstruirZipAsync(
            grupos, c => ("Trabajadores", c.Id.ToString()), _almacenamiento, NullLogger.Instance, CancellationToken.None, maximoBytes: 10);

        await construir.Should().ThrowAsync<PaqueteDocumentalDemasiadoPesadoException>();
    }

    [Fact]
    public async Task El_tope_se_cumple_sobre_el_zip_cerrado_con_su_indice_y_su_directorio_central()
    {
        // Un documento que entra y otro cuyo archivo no se abre: el zip lleva además el índice en la raíz.
        var grupos = new List<IReadOnlyList<ArmadoPaqueteDocumental.Copia>>
        {
            new[] { new ArmadoPaqueteDocumental.Copia(_epiAna.Id, _ana.Id, _epi.Id, _epiAna.ArchivoUrl!) },
            new[] { new ArmadoPaqueteDocumental.Copia(Guid.NewGuid(), _luis.Id, _epi.Id, "no-existe.pdf") },
        };
        var conIndice = 0;
        Task<(byte[] Bytes, IReadOnlyList<(Guid DocumentoId, Guid TipoDocumentoId)> Documentos)?> Construir(long? tope) =>
            ArmadoPaqueteDocumental.ConstruirZipAsync(
                grupos, c => ("Trabajadores", c.Id.ToString()), _almacenamiento, NullLogger.Instance, CancellationToken.None, tope,
                _ => { conIndice++; return ("LEEME.txt", "Entrega de EPI de Luis Perez: no se pudo abrir."); });

        var tamanoCompleto = (await Construir(null))!.Value.Bytes.LongLength;

        (await Construir(tamanoCompleto))!.Value.Bytes.LongLength.Should().Be(tamanoCompleto, "justo en el tope se entrega entero");
        var unByteMenos = () => Construir(tamanoCompleto - 1);
        await unByteMenos.Should().ThrowAsync<PaqueteDocumentalDemasiadoPesadoException>(
            "el último byte que se entrega es del directorio central, escrito al cerrar el zip, después del último documento");
        conIndice.Should().Be(3, "las tres construcciones llegan a escribir el índice");
    }

    [Fact]
    public async Task Un_documento_grande_se_corta_durante_su_copia_sin_llegar_a_leerlo_entero()
    {
        var grande = new byte[4 * 1024 * 1024];
        new Random(7).NextBytes(grande); // incompresible: lo comprimido mide casi lo mismo
        var almacenamiento = new AlmacenamientoQueCuentaLecturas(grande);
        var grupos = new List<IReadOnlyList<ArmadoPaqueteDocumental.Copia>>
        {
            new[] { new ArmadoPaqueteDocumental.Copia(_epiAna.Id, _ana.Id, _epi.Id, "grande.pdf") },
        };

        var construir = () => ArmadoPaqueteDocumental.ConstruirZipAsync(
            grupos, c => ("Trabajadores", c.Id.ToString()), almacenamiento, NullLogger.Instance, CancellationToken.None, maximoBytes: 64 * 1024);

        await construir.Should().ThrowAsync<PaqueteDocumentalDemasiadoPesadoException>();
        almacenamiento.BytesLeidos.Should().BeLessThan(grande.Length / 4,
            "la escritura se corta en cuanto pasaría del tope, no al terminar de copiar y comprimir el documento");
    }

    [Theory]
    [InlineData(100, false)]
    [InlineData(101, true)]
    public void El_flujo_con_tope_no_deja_pasar_ni_un_byte_por_encima_del_tope(int bytes, bool excede)
    {
        var interior = new MemoryStream();
        var flujo = new FlujoConTope(interior, 100);
        flujo.Write(new byte[60]);

        var escribir = () => flujo.Write(new byte[bytes - 60]);

        if (excede)
        {
            escribir.Should().Throw<PaqueteDocumentalDemasiadoPesadoException>();
            interior.Length.Should().Be(60, "la escritura que pasaría del tope no escribe nada");
            flujo.Excedido.Should().BeTrue();
            flujo.Invoking(f => f.WriteByte(0)).Should().Throw<PaqueteDocumentalDemasiadoPesadoException>("pasado el tope, todo lo que siga también corta");
        }
        else
        {
            escribir.Should().NotThrow();
            interior.Length.Should().Be(100);
            flujo.Excedido.Should().BeFalse();
        }
    }

    [Fact]
    public void El_flujo_con_tope_cuenta_hasta_donde_llega_el_ultimo_byte_al_volver_atras()
    {
        var interior = new MemoryStream();
        var flujo = new FlujoConTope(interior, 100);
        flujo.Write(new byte[100]);

        flujo.Position = 10;
        flujo.Invoking(f => f.Write(new byte[90])).Should().NotThrow("reescribir dentro de lo ya escrito no alarga el zip");
        flujo.Invoking(f => f.WriteByte(0)).Should().Throw<PaqueteDocumentalDemasiadoPesadoException>();
        interior.Length.Should().Be(100);
    }

    [Fact]
    public async Task Un_paquete_de_Empresa_que_pasa_su_tope_de_bytes_responde_como_demasiado_pesado_y_no_registra_nada()
    {
        _maximoBytes = 10;

        (await DescargarAsync(_cliente.Id)).Error.Should().Be(AutorizacionPaqueteEmpresa.DemasiadoPesado);
        _registro.Registrados.Should().BeEmpty("no se entrega nada, así que no hay acceso que registrar");
    }

    [Fact]
    public void Sin_fijar_nada_el_servicio_de_produccion_aplica_el_tope_de_200_MiB()
    {
        ServicioSinTopeFijado().MaximoBytesZip.Should().Be(PaqueteDocumentalEmpresaService.MaximoBytes)
            .And.Be(200L * 1024 * 1024, "es el tope que usa producción: la inyección por constructor no pasa por el inicializador de los tests");
    }

    [Fact]
    public async Task Sin_Sesion_Privilegiada_el_resumen_permite_descargar()
    {
        (await ResumirAsync(_cliente.Id)).Valor.PuedeDescargar.Should().BeTrue();
    }

    // ---- Montaje

    private Task<Result<ResumenPaqueteEmpresaDto>> ResumirAsync(Guid? clienteId) =>
        Mediador().ResumirAsync(new ObtenerResumenPaqueteEmpresaQuery(_empresa.Id, clienteId));

    private Task<Result<PaqueteEmpresaDescargaDto>> DescargarAsync(Guid? clienteId) =>
        Mediador().DescargarAsync(new DescargarPaqueteEmpresaQuery(_empresa.Id, clienteId));

    private Handlers Mediador()
    {
        var servicio = new PaqueteDocumentalEmpresaService(
            _empresas, _trabajadores, _documentos, _tipos, _asignaciones, _centros, _calculo, _almacenamiento, _alcance,
            NullLogger<PaqueteDocumentalEmpresaService>.Instance)
        {
            MaximoBytesZip = _maximoBytes,
        };
        var usuario = new CurrentUserServiceFalso(rol: _rol);
        var sesion = new SesionPrivilegiadaFija(_sesionPrivilegiada);
        return new Handlers(
            new ObtenerResumenPaqueteEmpresaQueryHandler(_empresas, _alcance, usuario, sesion, servicio),
            new DescargarPaqueteEmpresaQueryHandler(_empresas, _alcance, usuario, sesion, servicio, _registro, _transaccion));
    }

    private PaqueteDocumentalEmpresaService ServicioSinTopeFijado() =>
        new(_empresas, _trabajadores, _documentos, _tipos, _asignaciones, _centros, _calculo, _almacenamiento, _alcance,
            NullLogger<PaqueteDocumentalEmpresaService>.Instance);

    private sealed record Handlers(ObtenerResumenPaqueteEmpresaQueryHandler Resumen, DescargarPaqueteEmpresaQueryHandler Descarga)
    {
        public Task<Result<ResumenPaqueteEmpresaDto>> ResumirAsync(ObtenerResumenPaqueteEmpresaQuery query) => Resumen.Handle(query, CancellationToken.None);
        public Task<Result<PaqueteEmpresaDescargaDto>> DescargarAsync(DescargarPaqueteEmpresaQuery query) => Descarga.Handle(query, CancellationToken.None);
    }

    private Documento DocumentoDeTrabajador(Trabajador trabajador, TipoDocumento tipo, DateOnly emision, VigenciaDocumento vigencia, string contenido)
    {
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, emision, vigencia, Guardar(contenido));
        _documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    private Documento DocumentoDeEmpresa(TipoDocumento tipo, VigenciaDocumento vigencia, string contenido)
    {
        var documento = Documento.DeEmpresa(_empresa.Id, tipo.Id, Hoy.AddDays(-10), vigencia, Guardar(contenido));
        _documentos.ListaDocumentos.Add(documento);
        return documento;
    }

    private string Guardar(string contenido) =>
        _almacenamiento.GuardarAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(contenido)), "doc.pdf").GetAwaiter().GetResult();

    private static TipoDocumento NuevoTipo(string nombre, AmbitoAplicacion ambito) =>
        new(nombre, vigenciaMeses: null, aplicaVencimientoAutomatico: false, orden: 1, ambito);

    private static Dictionary<string, string> LeerZip(byte[] contenido)
    {
        using var zip = new ZipArchive(new MemoryStream(contenido), ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var lector = new StreamReader(e.Open());
            return lector.ReadToEnd();
        });
    }

    private sealed class CalculoConParesFijos : ICalculoEstadoCentroService
    {
        private readonly List<ParDocumentalExigido> _pares = [];
        private readonly List<ParDocumentalDeEmpresaExigido> _paresDeEmpresa = [];

        public void Exigir(Centro centro, Trabajador trabajador, TipoDocumento tipo) =>
            _pares.Add(new ParDocumentalExigido(centro.Id, centro.ClienteId, trabajador.EmpresaId, trabajador.Id, tipo.Id, EstadoDocumento.Vigente, false, false));

        public void ExigirALaEmpresa(Centro centro, Empresa empresa, TipoDocumento tipo) =>
            _paresDeEmpresa.Add(new ParDocumentalDeEmpresaExigido(centro.Id, empresa.Id, tipo.Id, EstadoDocumento.Vigente, false, false));

        public Task<IReadOnlyList<ParDocumentalExigido>> ObtenerParesExigidosAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ParDocumentalExigido>>(_pares.Where(p => centroIds.Contains(p.CentroId)).ToList());

        public Task<IReadOnlyList<ParDocumentalDeEmpresaExigido>> ObtenerParesDeEmpresaExigidosAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ParDocumentalDeEmpresaExigido>>(_paresDeEmpresa.Where(p => centroIds.Contains(p.CentroId)).ToList());

        public Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Registros guardados: lo que se añade dentro de una <see cref="TransaccionQueDeshace"/> solo se queda si se confirma,
    /// como en la base. Anota también si cada registro llegó con la transacción abierta.
    /// </summary>
    private sealed class RegistroQueAnota(TransaccionQueDeshace transaccion) : IRegistroAccesoDocumentoSensibleService
    {
        public List<(Guid DocumentoId, TipoAccesoDocumentoSensible Tipo)> Registrados { get; } = [];
        public List<bool> ConTransaccionAbierta { get; } = [];
        public bool Falla { get; set; }

        /// <summary>Con valor, la llamada n-ésima (desde 1) falla en cerrado y las anteriores ya se han guardado.</summary>
        public int? FallaEnLaLlamada { get; set; }

        private int _llamadas;

        public Task RegistrarSiSensibleAsync(Guid documentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default)
        {
            _llamadas++;
            ConTransaccionAbierta.Add(transaccion.Abierta);
            if (Falla || _llamadas == FallaEnLaLlamada) throw new AccesoDocumentoSensibleNoRegistradoException(documentoId);
            Registrados.Add((documentoId, tipoAcceso));
            return Task.CompletedTask;
        }

        public Task RegistrarSiSensibleAsync(Guid recursoId, Guid tipoDocumentoId, TipoAccesoDocumentoSensible tipoAcceso, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Doble de <see cref="ITransaccionDeComando"/> que deshace como la base: si la operación lanza o devuelve un fallo, lo
    /// guardado dentro desaparece (<see cref="AlDeshacer"/>). Que el rollback de PostgreSQL deshaga de verdad lo prueba la
    /// integración de <c>TransaccionDeComando</c>.
    /// </summary>
    private sealed class TransaccionQueDeshace : ITransaccionDeComando
    {
        public bool Abierta { get; private set; }
        public int Ejecutadas { get; private set; }
        public int Confirmadas { get; private set; }
        public int Deshechas { get; private set; }
        public Func<int> Contar { get; set; } = () => 0;
        public Action<int> AlDeshacer { get; set; } = _ => { };

        public async Task<Result> EjecutarAsync(Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
        {
            Ejecutadas++;
            var antes = Contar();
            Abierta = true;
            try
            {
                var resultado = await operacion(cancellationToken);
                if (resultado.EsFallido) Deshacer(antes);
                else Confirmadas++;
                return resultado;
            }
            catch
            {
                Deshacer(antes);
                throw;
            }
            finally
            {
                Abierta = false;
            }
        }

        private void Deshacer(int antes)
        {
            Deshechas++;
            AlDeshacer(antes);
        }
    }

    /// <summary>Un único archivo, servido por un flujo que cuenta cuántos bytes se le leen.</summary>
    private sealed class AlmacenamientoQueCuentaLecturas(byte[] contenido) : IFileStorageService
    {
        private readonly FlujoQueCuenta _flujo = new(contenido);
        public long BytesLeidos => _flujo.Leidos;

        public Task<string> GuardarAsync(Stream contenidoNuevo, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => Task.FromResult<Stream>(_flujo);

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private sealed class FlujoQueCuenta(byte[] datos) : MemoryStream(datos, writable: false)
        {
            public long Leidos { get; private set; }

            public override int Read(byte[] buffer, int offset, int count) => Contar(base.Read(buffer, offset, count));
            public override int Read(Span<byte> buffer) => Contar(base.Read(buffer));

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                Task.FromResult(Read(buffer, offset, count));

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(Read(buffer.Span));

            public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
                CopiarAsync(destination, bufferSize, cancellationToken);

            private async Task CopiarAsync(Stream destino, int tamano, CancellationToken cancellationToken)
            {
                var bufer = new byte[tamano];
                int leidos;
                while ((leidos = Read(bufer, 0, bufer.Length)) > 0)
                    await destino.WriteAsync(bufer.AsMemory(0, leidos), cancellationToken);
            }

            private int Contar(int leidos)
            {
                Leidos += leidos;
                return leidos;
            }
        }
    }

    private sealed class SesionPrivilegiadaFija(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
    }
}
