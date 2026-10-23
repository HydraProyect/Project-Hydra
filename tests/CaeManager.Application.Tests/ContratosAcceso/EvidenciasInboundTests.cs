using System.Security.Cryptography;
using System.Transactions;
using CaeManager.Application.Common;
using CaeManager.Application.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.ContratosAcceso.LecturaInbound;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.ContratosAcceso;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.EvidenciasInbound;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CaeManager.Application.Tests.ContratosAcceso;

/// <summary>
/// Registro, cambios de estado y lectura de las copias de evidencia validada de Inbound M1 (PR 4) dentro del Tenant del
/// Cliente empresarial. Lo que se prueba aquí: la autoridad es la de <see cref="LecturaInboundAutoridad"/> y lo que no
/// pasa responde «no encontrado» sin llegar al lector; el lector y la copia del binario ocurren antes del único guardado
/// y sin transacción ambiental; la copia va al almacenamiento del Tenant del Cliente empresarial con su SHA-256; y una
/// evidencia de «solo conclusión de aptitud» nunca abre ni copia el binario. Lo que la base garantiza (RLS, CHECK,
/// privilegios, excepción de salud) lo prueba EvidenciasInboundBajoRuntimeTests.
/// </summary>
public class EvidenciasInboundTests
{
    private static readonly byte[] Contenido = [0x25, 0x50, 0x44, 0x46, 1, 2, 3];

    private readonly Guid _cliente = Guid.NewGuid();
    private readonly Guid _tenantContratista = Guid.NewGuid();
    private readonly List<string> _orden = [];
    private readonly ContratosAccesoQueryContextFalso _contratos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly LectorFalso _lector;
    private readonly AlmacenOriginalesInboundFalso _originales;
    private readonly AlmacenDelTenantFalso _almacen;
    private readonly RepositorioFalso _repositorio = new();
    private readonly RegistroFalso _registro = new();
    private readonly UnidadDeTrabajoFalsa _unidad;
    private readonly ContratoAcceso _contrato;
    private readonly TipoDocumento _tipoFormacion;
    private readonly TipoDocumento _tipoAptitud;
    private readonly Guid _documento = Guid.NewGuid();
    private readonly string _referenciaOriginal;

    public EvidenciasInboundTests()
    {
        _lector = new LectorFalso(_orden);
        _originales = new AlmacenOriginalesInboundFalso(_orden);
        _almacen = new AlmacenDelTenantFalso(_orden);
        _unidad = new UnidadDeTrabajoFalsa(_orden);

        var empresa = new Empresa("Contratista de prueba", "B12345674");
        _empresas.ListaEmpresas.Add(empresa);
        _contrato = ContratoAcceso.Crear(_cliente, Guid.NewGuid(), empresa.Id, new DateOnly(2026, 3, 1), null, null);
        _contrato.VincularTenantContratista(_tenantContratista);
        _contratos.ListaContratos.Add(_contrato);

        _tipoFormacion = new TipoDocumento("Formación PRL", 12, aplicaVencimientoAutomatico: true, orden: 1,
            AmbitoAplicacion.Trabajador, sensibilidad: SensibilidadDocumental.DatosPersonales);
        _tipoAptitud = new TipoDocumento("Certificado de aptitud", 12, aplicaVencimientoAutomatico: true, orden: 2,
            AmbitoAplicacion.Trabajador, sensibilidad: SensibilidadDocumental.CategoriaEspecialSalud);
        _tipoAptitud.EstablecerSoloConclusionAptitud(true);
        _tipos.ListaTiposDocumento.AddRange([_tipoFormacion, _tipoAptitud]);

        _referenciaOriginal = $"{_tenantContratista:N}/original.pdf";
        _originales.Archivos[(_tenantContratista, _referenciaOriginal)] = Contenido;
        _lector.Referencia = _referenciaOriginal;
        _lector.TenantPropietario = _tenantContratista;
        _lector.Documentos.Add(Documento(_documento, soloConclusion: false));
    }

    private static DocumentoInboundDto Documento(Guid id, bool soloConclusion, string? dni = "12345678Z", bool deTrabajador = true) =>
        new(id, deTrabajador ? Guid.NewGuid() : null, deTrabajador ? "Ana" : null, deTrabajador ? "Pérez" : null,
            deTrabajador ? dni : null, "Tipo del original", new DateOnly(2026, 3, 1), new DateOnly(2027, 3, 1),
            EstadoVigenciaDocumento.VenceEnFecha, soloConclusion);

    private RegistrarEvidenciaInboundCommandHandler Registrar(string? rol = "GestorCae", Guid? clienteVisible = null) =>
        new(_contratos, _tipos, _empresas, Alcance(clienteVisible ?? _cliente), new UsuarioConRol(rol), _lector, _originales,
            _almacen, _repositorio, _unidad, new RelojFijo(new DateTimeOffset(2026, 10, 23, 9, 0, 0, TimeSpan.Zero)),
            new EvidenciasQueryContextFalso(_repositorio.Agregadas), _registro);

    private static AlcanceDatosServiceFalso Alcance(Guid clienteVisible) =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: [clienteVisible]);

    private RegistrarEvidenciaInboundCommand Comando(TipoDocumento? tipo = null, ConclusionAptitud? conclusion = null, Guid? documento = null) =>
        new(_contrato.Id, documento ?? _documento, (tipo ?? _tipoFormacion).Id, conclusion);

    [Fact]
    public async Task Una_evidencia_no_medica_copia_el_original_al_almacenamiento_del_Tenant_del_Cliente_empresarial_con_su_huella()
    {
        var resultado = await Registrar().Handle(Comando(), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var evidencia = _repositorio.Agregadas.Should().ContainSingle().Subject;
        evidencia.Id.Should().Be(resultado.Valor);
        _originales.Aperturas.Should().Equal([(_tenantContratista, _referenciaOriginal)],
            "el original se abre con la carpeta y la clave de su Tenant propietario, el del contrato");
        var copia = _almacen.Guardados.Should().ContainSingle().Subject;
        copia.Contenido.Should().Equal(Contenido);
        evidencia.ArchivoUrl.Should().Be(copia.Referencia).And.NotBe(_referenciaOriginal,
            "la evidencia apunta a la copia del Tenant del Cliente empresarial, nunca al original");
        evidencia.HuellaSha256.Should().Be(Convert.ToHexString(SHA256.HashData(Contenido)).ToLowerInvariant());
        (evidencia.TenantOrigenId, evidencia.DocumentoOrigenId, evidencia.AutorizacionId, evidencia.ContratoAccesoId)
            .Should().Be((_tenantContratista, _documento, _lector.Autorizacion!.AutorizacionId, _contrato.Id));
        (evidencia.SujetoNombre, evidencia.SujetoDocumentoIdentidad).Should().Be(("Ana Pérez", "12345678Z"));
        evidencia.TipoDocumentoId.Should().Be(_tipoFormacion.Id);
    }

    [Fact]
    public async Task El_lector_y_la_copia_ocurren_antes_del_unico_guardado_y_sin_transaccion_ambiental()
    {
        await Registrar().Handle(Comando(), CancellationToken.None);

        _orden.Should().Equal("Autorizacion", "Documentos", "Archivo", "AbrirOriginal", "GuardarCopia", "SaveChanges");
        _lector.HabiaTransaccionAmbiental.Should().BeFalse(
            "cada lectura deja su traza en el Tenant propietario; dentro de una transacción un rollback la borraría");
    }

    [Theory]
    [InlineData(ConclusionAptitud.Apto)]
    [InlineData(ConclusionAptitud.AptoConRestricciones)]
    [InlineData(ConclusionAptitud.NoApto)]
    public async Task Una_evidencia_de_solo_conclusion_nunca_abre_ni_copia_el_binario(ConclusionAptitud conclusion)
    {
        var resultado = await Registrar().Handle(Comando(_tipoAptitud, conclusion), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        var evidencia = _repositorio.Agregadas.Should().ContainSingle().Subject;
        evidencia.SoloConclusionAptitud.Should().BeTrue();
        evidencia.ConclusionAptitud.Should().Be(conclusion);
        evidencia.ArchivoUrl.Should().BeNull();
        evidencia.HuellaSha256.Should().BeNull();
        (evidencia.SujetoNombre, evidencia.SujetoDocumentoIdentidad).Should().Be(("Ana Pérez", "12345678Z"));
        _originales.Aperturas.Should().BeEmpty("el validador abre el certificado, la aplicación no lee ni copia el binario");
        _almacen.Guardados.Should().BeEmpty();
        _lector.Llamadas.Should().NotContain("Archivo", "ni siquiera se pide la referencia del archivo");
    }

    [Fact]
    public async Task Un_original_de_tipo_marcado_es_de_solo_conclusion_aunque_el_tipo_elegido_no_lo_sea()
    {
        var documento = Guid.NewGuid();
        _lector.Documentos.Add(Documento(documento, soloConclusion: true));

        var sinConclusion = await Registrar().Handle(Comando(_tipoFormacion, documento: documento), CancellationToken.None);
        var conConclusion = await Registrar().Handle(Comando(_tipoFormacion, ConclusionAptitud.Apto, documento), CancellationToken.None);

        sinConclusion.Error.Should().Be(EvidenciaInboundErrores.FaltaConclusion);
        conConclusion.EsExitoso.Should().BeTrue();
        _repositorio.Agregadas.Should().ContainSingle().Which.ArchivoUrl.Should().BeNull();
        _originales.Aperturas.Should().BeEmpty();
        _almacen.Guardados.Should().BeEmpty();
    }

    [Fact]
    public async Task La_conclusion_sobra_en_un_tipo_no_medico_y_falta_en_uno_medico()
    {
        (await Registrar().Handle(Comando(_tipoFormacion, ConclusionAptitud.Apto), CancellationToken.None))
            .Error.Should().Be(EvidenciaInboundErrores.ConclusionSobra);
        (await Registrar().Handle(Comando(_tipoAptitud), CancellationToken.None))
            .Error.Should().Be(EvidenciaInboundErrores.FaltaConclusion);
        _repositorio.Agregadas.Should().BeEmpty();
        _almacen.Guardados.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_evidencia_de_solo_conclusion_sin_documento_de_identidad_no_se_registra()
    {
        var documento = Guid.NewGuid();
        _lector.Documentos.Add(Documento(documento, soloConclusion: true, dni: null));

        var resultado = await Registrar().Handle(Comando(_tipoAptitud, ConclusionAptitud.Apto, documento), CancellationToken.None);

        resultado.Error.Should().Be(EvidenciaInboundErrores.SinDocumentoIdentidad);
        _repositorio.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Documento_de_Empresa_toma_como_sujeto_la_Empresa_contratista_local_del_contrato()
    {
        var documento = Guid.NewGuid();
        _lector.Documentos.Add(Documento(documento, soloConclusion: false, deTrabajador: false));

        (await Registrar().Handle(Comando(documento: documento), CancellationToken.None)).EsExitoso.Should().BeTrue();

        var evidencia = _repositorio.Agregadas.Should().ContainSingle().Subject;
        (evidencia.TrabajadorOrigenId, evidencia.SujetoNombre, evidencia.SujetoDocumentoIdentidad)
            .Should().Be(((Guid?)null, "Contratista de prueba", "B12345674"));
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Un_rol_que_no_valida_responde_no_encontrado_sin_llamar_al_lector(string? rol)
    {
        var resultado = await Registrar(rol).Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(EvidenciaInboundErrores.NoEncontrada);
        _orden.Should().BeEmpty("sin autoridad no se llama a la base ni se abre nada");
    }

    [Fact]
    public async Task Un_contrato_fuera_de_cartera_responde_no_encontrado_sin_llamar_al_lector()
    {
        var resultado = await Registrar(clienteVisible: Guid.NewGuid()).Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(EvidenciaInboundErrores.NoEncontrada);
        _orden.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_autorizacion_vigente_o_con_un_Documento_fuera_de_ella_responde_no_encontrado()
    {
        _lector.Autorizacion = _lector.Autorizacion! with { VigenteHoy = false, Revocada = true };
        (await Registrar().Handle(Comando(), CancellationToken.None)).Error.Should().Be(EvidenciaInboundErrores.NoEncontrada);

        _lector.Autorizacion = _lector.Autorizacion with { VigenteHoy = true, Revocada = false };
        (await Registrar().Handle(Comando(documento: Guid.NewGuid()), CancellationToken.None))
            .Error.Should().Be(EvidenciaInboundErrores.NoEncontrada);

        _repositorio.Agregadas.Should().BeEmpty();
        _originales.Aperturas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_referencia_que_no_es_de_la_carpeta_del_Tenant_del_contrato_no_se_abre()
    {
        _lector.Referencia = $"{Guid.NewGuid():N}/original.pdf";

        var resultado = await Registrar().Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(EvidenciaInboundErrores.OriginalSinArchivo);
        _originales.Aperturas.Should().BeEmpty();
        _almacen.Guardados.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_el_guardado_falla_la_copia_se_elimina_y_no_queda_un_binario_huerfano()
    {
        _unidad.ExcepcionAlGuardar = new InvalidOperationException("fallo de guardado");

        var registrar = () => Registrar().Handle(Comando(), CancellationToken.None);

        await registrar.Should().ThrowAsync<InvalidOperationException>().WithMessage("fallo de guardado");
        _almacen.Guardados.Should().ContainSingle();
        _almacen.Eliminados.Should().Equal(_almacen.Guardados[0].Referencia);
    }

    [Fact]
    public async Task Si_tambien_falla_el_borrado_de_la_copia_se_registra_y_se_propaga_el_error_original()
    {
        _unidad.ExcepcionAlGuardar = new InvalidOperationException("fallo de guardado");
        var errorAlEliminar = new IOException("fallo al borrar");
        _almacen.ExcepcionAlEliminar = errorAlEliminar;

        var registrar = () => Registrar().Handle(Comando(), CancellationToken.None);

        await registrar.Should().ThrowAsync<InvalidOperationException>().WithMessage("fallo de guardado",
            "el error del borrado no tapa el que explica el fallo");
        _almacen.Eliminados.Should().ContainSingle();
        _registro.Entradas.Should().ContainSingle(e => e.Nivel == LogLevel.Error)
            .Which.Excepcion.Should().BeSameAs(errorAlEliminar, "la copia huérfana queda registrada");
    }

    [Fact]
    public async Task Una_apertura_marcada_cuando_el_listado_no_lo_estaba_aborta_sin_copiar()
    {
        // El listado dijo «sin marca» y la apertura, leída en otra sentencia, viene marcada: el Tenant propietario marcó su
        // tipo entre las dos lecturas. El binario es de un certificado de aptitud y no se copia. (Un listado marcado nunca
        // llega a pedir la apertura: lo cubre Una_evidencia_de_solo_conclusion_nunca_abre_ni_copia_el_binario.)
        _lector.SoloConclusionEnLaApertura = true;

        var resultado = await Registrar().Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(EvidenciaInboundErrores.NoEncontrada, "error genérico: no dice qué cambió en el otro Tenant");
        _originales.Aperturas.Should().BeEmpty("no se lee el binario");
        _almacen.Guardados.Should().BeEmpty("ni se copia");
        _repositorio.Agregadas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Documento_ya_registrado_en_el_contrato_responde_ya_registrada_sin_leer_ni_copiar()
    {
        (await Registrar().Handle(Comando(), CancellationToken.None)).EsExitoso.Should().BeTrue();
        _orden.Clear();

        var otra = await Registrar().Handle(Comando(), CancellationToken.None);

        otra.Error.Should().Be(EvidenciaInboundErrores.YaRegistrada);
        _orden.Should().BeEmpty("ni lectura del otro Tenant, ni apertura, ni copia, ni guardado");
        _almacen.Guardados.Should().ContainSingle("solo la copia de la primera");
        _repositorio.Agregadas.Should().ContainSingle();
    }

    [Fact]
    public async Task Un_Documento_de_Empresa_con_un_tipo_de_solo_conclusion_no_se_admite_y_no_es_falta_de_identidad()
    {
        var documento = Guid.NewGuid();
        _lector.Documentos.Add(Documento(documento, soloConclusion: false, deTrabajador: false));

        var resultado = await Registrar().Handle(Comando(_tipoAptitud, ConclusionAptitud.Apto, documento), CancellationToken.None);

        resultado.Error.Should().Be(EvidenciaInboundErrores.SoloConclusionNoAdmitida);
        _repositorio.Agregadas.Should().BeEmpty();
        _lector.Llamadas.Should().NotContain("Archivo");
    }

    [Theory]
    [InlineData("12345678Z")]
    [InlineData("X1234567L")]
    [InlineData("PAB123456")]
    [InlineData("AB 123-45")]
    public async Task La_conclusion_admite_cualquier_documento_de_identidad_no_vacio_sin_validar_su_formato(string documentoIdentidad)
    {
        var documento = Guid.NewGuid();
        _lector.Documentos.Add(Documento(documento, soloConclusion: true, dni: documentoIdentidad));

        var resultado = await Registrar().Handle(Comando(_tipoAptitud, ConclusionAptitud.Apto, documento), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(documentoIdentidad);
        _repositorio.Agregadas.Should().ContainSingle().Which.SujetoDocumentoIdentidad.Should().Be(documentoIdentidad);
    }

    [Fact]
    public void Sin_documento_de_identidad_el_mensaje_dice_que_lo_corrige_la_Empresa_contratista_en_la_ficha_del_Trabajador()
    {
        EvidenciaInboundErrores.SinDocumentoIdentidad.Mensaje.Should()
            .Contain("Empresa contratista").And.Contain("ficha del Trabajador");
    }

    [Fact]
    public async Task Una_apertura_con_un_Tenant_propietario_que_no_es_el_del_contrato_no_se_abre()
    {
        var otroTenant = Guid.NewGuid();
        _lector.TenantPropietario = otroTenant;
        _lector.Referencia = $"{otroTenant:N}/original.pdf";
        _originales.Archivos[(otroTenant, _lector.Referencia)] = Contenido;

        var resultado = await Registrar().Handle(Comando(), CancellationToken.None);

        resultado.Error.Should().Be(EvidenciaInboundErrores.OriginalSinArchivo);
        _originales.Aperturas.Should().BeEmpty();
        _almacen.Guardados.Should().BeEmpty();
    }

    [Fact]
    public async Task Rechazar_y_validar_aplican_la_misma_autoridad_y_solo_las_transiciones_existentes()
    {
        await Registrar().Handle(Comando(), CancellationToken.None);
        var evidencia = _repositorio.Agregadas.Single();
        var reloj = new RelojFijo(new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero));

        RechazarEvidenciaInboundCommandHandler Rechazar(string rol, Guid cliente) =>
            new(_repositorio, _contratos, Alcance(cliente), new UsuarioConRol(rol), _unidad, reloj);
        ValidarEvidenciaInboundCommandHandler Validar(string rol, Guid cliente) =>
            new(_repositorio, _contratos, Alcance(cliente), new UsuarioConRol(rol), _unidad, reloj);
        var rechazo = new RechazarEvidenciaInboundCommand(evidencia.Id, CausaRechazoAcreditacion.DatosErroneos, "El NIE no coincide");

        (await Rechazar("Consulta", _cliente).Handle(rechazo, CancellationToken.None)).Error.Should().Be(EvidenciaInboundErrores.NoEncontrada);
        (await Rechazar("GestorCae", Guid.NewGuid()).Handle(rechazo, CancellationToken.None)).Error.Should().Be(EvidenciaInboundErrores.NoEncontrada);
        (await Validar("GestorCae", _cliente).Handle(new(evidencia.Id), CancellationToken.None)).Error.Should().Be(EvidenciaInboundErrores.EstadoNoAdmite);
        evidencia.Estado.Should().Be(EstadoAcreditacion.Aceptada);

        (await Rechazar("GestorCae", _cliente).Handle(rechazo, CancellationToken.None)).EsExitoso.Should().BeTrue();
        evidencia.Estado.Should().Be(EstadoAcreditacion.Rechazada);
        (await Rechazar("GestorCae", _cliente).Handle(rechazo, CancellationToken.None)).Error.Should().Be(EvidenciaInboundErrores.EstadoNoAdmite);

        (await Validar("GestorCae", _cliente).Handle(new(evidencia.Id), CancellationToken.None)).EsExitoso.Should().BeTrue();
        evidencia.Estado.Should().Be(EstadoAcreditacion.Aceptada);
        _orden.Count(o => o is "Autorizacion" or "Documentos" or "Archivo").Should().Be(3, "los cambios de estado no leen el otro Tenant");
    }

    [Fact]
    public async Task Las_consultas_responden_no_encontrado_sin_autoridad_y_no_exponen_la_referencia_del_archivo()
    {
        await Registrar().Handle(Comando(), CancellationToken.None);
        var evidencia = _repositorio.Agregadas.Single();
        var contexto = new EvidenciasQueryContextFalso(_repositorio.Agregadas);

        ObtenerEvidenciasInboundQueryHandler Lista(string rol, Guid cliente) =>
            new(contexto, _tipos, _contratos, Alcance(cliente), new UsuarioConRol(rol));
        ObtenerEvidenciaInboundPorIdQueryHandler PorId(string rol, Guid cliente) =>
            new(contexto, _tipos, _contratos, Alcance(cliente), new UsuarioConRol(rol));

        (await Lista("Consulta", _cliente).Handle(new(_contrato.Id), CancellationToken.None)).Should().BeNull();
        (await Lista("GestorCae", Guid.NewGuid()).Handle(new(_contrato.Id), CancellationToken.None)).Should().BeNull();
        (await PorId("GestorCae", Guid.NewGuid()).Handle(new(evidencia.Id), CancellationToken.None)).Should().BeNull();
        (await PorId("GestorCae", _cliente).Handle(new(Guid.NewGuid()), CancellationToken.None)).Should().BeNull();

        var lista = await Lista("GestorCae", _cliente).Handle(new(_contrato.Id), CancellationToken.None);
        var dto = lista.Should().ContainSingle().Subject;
        dto.Id.Should().Be(evidencia.Id);
        dto.TieneArchivo.Should().BeTrue();
        dto.TipoDocumentoNombre.Should().Be("Formación PRL");
        typeof(EvidenciaInboundDto).GetProperties().Select(p => p.Name).Should().NotContain("ArchivoUrl");
        (await PorId("GestorCae", _cliente).Handle(new(evidencia.Id), CancellationToken.None))!.Id.Should().Be(evidencia.Id);
    }

    private sealed class LectorFalso(List<string> orden) : ILectorDocumentosInbound
    {
        public List<string> Llamadas { get; } = [];
        public List<DocumentoInboundDto> Documentos { get; } = [];
        public string? Referencia { get; set; }
        public Guid TenantPropietario { get; set; }
        public bool SoloConclusionEnLaApertura { get; set; }
        public bool HabiaTransaccionAmbiental { get; private set; }

        public AutorizacionInboundDto? Autorizacion { get; set; } =
            new(Guid.NewGuid(), new DateOnly(2026, 3, 1), null, false, VigenteHoy: true, true, 1);

        private void Apuntar(string llamada)
        {
            HabiaTransaccionAmbiental |= Transaction.Current is not null;
            Llamadas.Add(llamada);
            orden.Add(llamada);
        }

        public Task<AutorizacionInboundDto?> AutorizacionDelContratoAsync(Guid contratoAccesoId, CancellationToken cancellationToken = default)
        {
            Apuntar("Autorizacion");
            return Task.FromResult(Autorizacion);
        }

        public Task<IReadOnlyList<DocumentoInboundDto>> DocumentosAutorizadosAsync(Guid contratoAccesoId, CancellationToken cancellationToken = default)
        {
            Apuntar("Documentos");
            return Task.FromResult<IReadOnlyList<DocumentoInboundDto>>(Documentos.ToList());
        }

        public Task<ArchivoInboundDto?> ArchivoAutorizadoAsync(Guid contratoAccesoId, Guid documentoId, CancellationToken cancellationToken = default)
        {
            Apuntar("Archivo");
            return Task.FromResult<ArchivoInboundDto?>(new(documentoId, TenantPropietario, Referencia, SoloConclusionEnLaApertura));
        }
    }

    private sealed class AlmacenDelTenantFalso(List<string> orden) : IFileStorageService
    {
        public List<(string Referencia, byte[] Contenido)> Guardados { get; } = [];
        public List<string> Eliminados { get; } = [];

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
        {
            using var memoria = new MemoryStream();
            contenido.CopyTo(memoria);
            var referencia = $"tenant-del-cliente-empresarial/{Guid.NewGuid():N}{Path.GetExtension(nombreArchivoOriginal)}";
            Guardados.Add((referencia, memoria.ToArray()));
            orden.Add("GuardarCopia");
            return Task.FromResult(referencia);
        }

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("El registro no abre nada del almacenamiento del Tenant del Cliente empresarial.");

        public Exception? ExcepcionAlEliminar { get; set; }

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
        {
            Eliminados.Add(identificador);
            return ExcepcionAlEliminar is { } excepcion ? throw excepcion : Task.CompletedTask;
        }
    }

    private sealed class RegistroFalso : ILogger<RegistrarEvidenciaInboundCommandHandler>
    {
        public List<(LogLevel Nivel, Exception? Excepcion)> Entradas { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entradas.Add((logLevel, exception));
    }

    private sealed class UnidadDeTrabajoFalsa(List<string> orden) : IUnitOfWork
    {
        public Exception? ExcepcionAlGuardar { get; set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            orden.Add("SaveChanges");
            return ExcepcionAlGuardar is { } excepcion ? throw excepcion : Task.FromResult(1);
        }
    }

    private sealed class RepositorioFalso : IEvidenciaInboundRepository
    {
        public List<EvidenciaInbound> Agregadas { get; } = [];

        public Task<EvidenciaInbound?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Agregadas.FirstOrDefault(e => e.Id == id));

        public void Agregar(EvidenciaInbound evidencia) => Agregadas.Add(evidencia);
    }

    private sealed class EvidenciasQueryContextFalso(List<EvidenciaInbound> evidencias) : IEvidenciasInboundQueryContext
    {
        public IQueryable<EvidenciaInbound> EvidenciasInbound => new TestAsyncQueryable<EvidenciaInbound>(evidencias.AsQueryable());
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }

    private sealed class UsuarioConRol(string? rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rol);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
