using System.Security.Cryptography;
using CaeManager.Application.Common;
using CaeManager.Application.ContratosAcceso.EvidenciasInbound;
using CaeManager.Domain.AutorizacionesLectura;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.ContratosAcceso;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.EvidenciasInbound;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.AutorizacionesLectura;

/// <summary>
/// Copias de evidencia validada de Inbound M1 (PR 4) bajo <c>cae_app_runtime</c> con contexto firmado. X es el Tenant del
/// Cliente empresarial (lector y Tenant propietario de las copias); Y, el Tenant de la Empresa contratista (Tenant
/// propietario de los originales y de la autorización); Z, otro Tenant. Lo que prueba la base: EvidenciasInbound tiene RLS
/// forzada con la política de aislamiento; el runtime inserta y cambia solo el estado, nunca borra ni reescribe la copia, y
/// Soporte solo lee; el CHECK médico y el trigger impiden archivo en una evidencia de solo conclusión de aptitud; la lectura
/// entre Tenants deja pasar un tipo de salud solo si lleva la marca; el registro de verdad copia el binario al
/// almacenamiento de X con su huella y no abre nada en la médica; y revocar la autorización conserva las copias y deja la
/// lectura en cero filas. El resto de la lectura (sustituidos, IncluyeDocumentosEmpresa, Trabajadores eliminados o
/// anonimizados, contexto firmado, traza) sigue en LecturaInboundBajoRuntimeTests, sin cambios.
/// </summary>
public class EvidenciasInboundBajoRuntimeTests : IAsyncLifetime
{
    private static readonly byte[] ContenidoOriginal = "%PDF formación PRL de Ana"u8.ToArray();

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly string _rutaArchivos = Path.Combine(Path.GetTempPath(), $"caemanager-evidencias-{Guid.NewGuid():N}");
    private readonly IDataProtectionProvider _proteccion = new EphemeralDataProtectionProvider();
    private readonly Tenant _lector = new("Tenant del Cliente empresarial");
    private readonly Tenant _contratista = new("Tenant de la Empresa contratista");
    private readonly Tenant _otro = new("Tenant de otro Cliente empresarial");
    private readonly Guid _usuarioLector = Guid.NewGuid();
    private readonly Guid _usuarioContratista = Guid.NewGuid();
    private readonly Guid _usuarioOtro = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();

    private Guid _cliente;
    private ContratoAcceso _contrato = null!;
    private Guid _tipoFormacionX;
    private Guid _tipoAptitudX;
    private Guid _autorizacion;
    private Guid _formacion;
    private Guid _aptitudMarcada;
    private Guid _informeSinMarca;
    private string _referenciaOriginal = null!;

    public async Task InitializeAsync()
    {
        await using (var contexto = Contexto(_lector.Id))
        {
            await contexto.Database.MigrateAsync();
            contexto.Tenants.AddRange(_lector, _contratista, _otro);
            var roles = await contexto.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);
            Cuenta(contexto, _usuarioLector, _lector.Id, roles[Roles.GestorCae]);
            Cuenta(contexto, _usuarioContratista, _contratista.Id, roles[Roles.Administrador]);
            Cuenta(contexto, _usuarioOtro, _otro.Id, roles[Roles.Administrador]);

            var cliente = Empresa.CrearComoCliente("Cliente empresarial", "B10380186", false, null, null);
            var contratistaLocal = new Empresa("Empresa contratista", "B10380194");
            contexto.Empresas.AddRange(cliente, contratistaLocal);
            var centro = new Centro(cliente.Id, cliente.Id, "Centro de trabajo del Cliente empresarial");
            contexto.Centros.Add(centro);
            var formacionX = new TipoDocumento("Formación PRL", null, false, 1, AmbitoAplicacion.Trabajador, sensibilidad: SensibilidadDocumental.DatosPersonales);
            var aptitudX = new TipoDocumento("Certificado de aptitud", null, false, 2, AmbitoAplicacion.Trabajador, sensibilidad: SensibilidadDocumental.CategoriaEspecialSalud);
            aptitudX.EstablecerSoloConclusionAptitud(true);
            contexto.TiposDocumento.AddRange(formacionX, aptitudX);
            await contexto.SaveChangesAsync();

            _contrato = ContratoAcceso.Crear(cliente.Id, centro.Id, contratistaLocal.Id, _hoy.AddDays(-10), null, null);
            _contrato.VincularTenantContratista(_contratista.Id);
            contexto.ContratosAcceso.Add(_contrato);
            await contexto.SaveChangesAsync();
            (_cliente, _tipoFormacionX, _tipoAptitudX) = (cliente.Id, formacionX.Id, aptitudX.Id);
        }

        // El original de verdad, cifrado con la clave del Tenant de la Empresa contratista en su carpeta.
        _referenciaOriginal = await Almacen(_contratista.Id).GuardarAsync(new MemoryStream(ContenidoOriginal), "formacion.pdf");

        await using (var contexto = Contexto(_contratista.Id))
        {
            var propia = new Empresa("Empresa contratista", "B10380194");
            contexto.Empresas.Add(propia);
            var general = new TipoDocumento("Formación PRL", null, false, 1, AmbitoAplicacion.Trabajador, sensibilidad: SensibilidadDocumental.DatosPersonales);
            var aptitud = new TipoDocumento("Certificado de aptitud", null, false, 2, AmbitoAplicacion.Trabajador, sensibilidad: SensibilidadDocumental.CategoriaEspecialSalud);
            aptitud.EstablecerSoloConclusionAptitud(true);
            var informe = new TipoDocumento("Informe médico", null, false, 3, AmbitoAplicacion.Trabajador, sensibilidad: SensibilidadDocumental.CategoriaEspecialSalud);
            contexto.TiposDocumento.AddRange(general, aptitud, informe);
            await contexto.SaveChangesAsync();

            var ana = Trabajador.DeEmpresa(propia.Id, "Ana", "Pérez", "12345678Z");
            contexto.Trabajadores.Add(ana);
            await contexto.SaveChangesAsync();

            var documentos = new[]
            {
                Documento.DeTrabajador(ana.Id, general.Id, _hoy.AddMonths(-2), VigenciaDocumento.VenceEl(_hoy.AddYears(1)), _referenciaOriginal),
                Documento.DeTrabajador(ana.Id, aptitud.Id, _hoy.AddMonths(-1), VigenciaDocumento.VenceEl(_hoy.AddYears(1)), $"{_contratista.Id:N}/aptitud.pdf"),
                Documento.DeTrabajador(ana.Id, informe.Id, _hoy.AddMonths(-1), VigenciaDocumento.NoCaduca, $"{_contratista.Id:N}/informe.pdf"),
            };
            contexto.Documentos.AddRange(documentos);
            var autorizacion = AutorizacionLecturaDocumental.Conceder(
                _lector.Id, _contrato.Id, true, _hoy.AddDays(-10), null, _usuarioContratista, DateTime.UtcNow);
            contexto.AutorizacionesLecturaDocumental.Add(autorizacion);
            await contexto.SaveChangesAsync();
            contexto.AutorizacionesLecturaTrabajadores.Add(autorizacion.AsignarTrabajador(ana.Id));
            await contexto.SaveChangesAsync();

            (_autorizacion, _formacion, _aptitudMarcada, _informeSinMarca) =
                (autorizacion.Id, documentos[0].Id, documentos[1].Id, documentos[2].Id);
        }
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadena);
        if (Directory.Exists(_rutaArchivos)) Directory.Delete(_rutaArchivos, recursive: true);
    }

    [Fact]
    public async Task EvidenciasInbound_tiene_RLS_forzada_con_la_politica_de_aislamiento()
    {
        await using var propietario = new NpgsqlConnection(_cadena);
        await propietario.OpenAsync();
        await using var orden = new NpgsqlCommand(
            """
            SELECT c.relrowsecurity, c.relforcerowsecurity,
                   (SELECT array_agg(p.polname::text ORDER BY p.polname) FROM pg_policy p WHERE p.polrelid = c.oid)
            FROM pg_class c WHERE c.oid = 'public."EvidenciasInbound"'::regclass;
            """, propietario);
        await using var lector = await orden.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();

        lector.GetBoolean(0).Should().BeTrue("ENABLE ROW LEVEL SECURITY");
        lector.GetBoolean(1).Should().BeTrue("FORCE ROW LEVEL SECURITY");
        lector.GetFieldValue<string[]>(2).Should().Equal("aislamiento_tenant");
    }

    [Fact]
    public async Task El_runtime_inserta_y_cambia_solo_el_estado_nunca_borra_y_Soporte_solo_lee()
    {
        await using var propietario = new NpgsqlConnection(_cadena);
        await propietario.OpenAsync();

        async Task<bool> Tabla(string rol, string privilegio)
        {
            await using var orden = new NpgsqlCommand(
                "SELECT has_table_privilege(@rol, 'public.\"EvidenciasInbound\"', @privilegio)", propietario);
            orden.Parameters.AddWithValue("rol", rol);
            orden.Parameters.AddWithValue("privilegio", privilegio);
            return (bool)(await orden.ExecuteScalarAsync())!;
        }

        async Task<bool> Columna(string rol, string columna)
        {
            await using var orden = new NpgsqlCommand(
                "SELECT has_column_privilege(@rol, 'public.\"EvidenciasInbound\"', @columna, 'UPDATE')", propietario);
            orden.Parameters.AddWithValue("rol", rol);
            orden.Parameters.AddWithValue("columna", columna);
            return (bool)(await orden.ExecuteScalarAsync())!;
        }

        (await Tabla("cae_app_runtime", "SELECT")).Should().BeTrue();
        (await Tabla("cae_app_runtime", "INSERT")).Should().BeTrue();
        (await Tabla("cae_app_runtime", "DELETE")).Should().BeFalse("la retención (DI-11) no está decidida: ningún camino borra");
        (await Tabla("cae_app_runtime", "TRUNCATE")).Should().BeFalse();
        foreach (var columna in new[] { "Estado", "CausaRechazo", "MotivoRechazo", "RechazadaPorUsuarioId", "RechazadaEnUtc", "ValidadaPorUsuarioId", "ValidadaEnUtc", "Version" })
            (await Columna("cae_app_runtime", columna)).Should().BeTrue(columna);
        foreach (var columna in new[]
                 {
                     "TenantId", "TenantOrigenId", "DocumentoOrigenId", "AutorizacionId", "ContratoAccesoId", "TipoDocumentoId",
                     "SujetoNombre", "SujetoDocumentoIdentidad", "ArchivoUrl", "HuellaSha256", "SoloConclusionAptitud",
                     "ConclusionAptitud", "FechaEmision", "FechaVencimiento", "EstaEliminado", "EliminadoEnUtc",
                 })
            (await Columna("cae_app_runtime", columna)).Should().BeFalse($"{columna}: la copia y su procedencia no se reescriben");

        (await Tabla("cae_app_soporte", "SELECT")).Should().BeTrue();
        foreach (var privilegio in new[] { "INSERT", "UPDATE", "DELETE" })
            (await Tabla("cae_app_soporte", privilegio)).Should().BeFalse($"Soporte: {privilegio}");
        foreach (var privilegio in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
            (await Tabla("cae_app_aprovisionamiento", privilegio)).Should().BeFalse($"aprovisionamiento: {privilegio}");

        // Y de verdad bajo runtime con contexto firmado: DELETE y UPDATE de la instantánea responden 42501.
        await RegistrarAsync(_formacion, _tipoFormacionX);
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        foreach (var sentencia in new[]
                 {
                     "DELETE FROM \"EvidenciasInbound\"",
                     "UPDATE \"EvidenciasInbound\" SET \"SujetoNombre\" = 'Otra persona'",
                     "UPDATE \"EvidenciasInbound\" SET \"ArchivoUrl\" = NULL, \"HuellaSha256\" = NULL",
                 })
        {
            (await FluentActions.Awaiting(() => contexto.Database.ExecuteSqlRawAsync(sentencia)).Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, sentencia);
        }
    }

    [Fact]
    public async Task Un_tipo_de_salud_marcado_se_lista_y_se_abre_y_uno_sin_marca_nunca()
    {
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var lector = new LectorDocumentosInboundEnBase(contexto);

        var documentos = await lector.DocumentosAutorizadosAsync(_contrato.Id);

        documentos.Select(d => d.DocumentoId).Should().BeEquivalentTo([_formacion, _aptitudMarcada],
            "el certificado de aptitud de un tipo marcado pasa; el informe médico, de un tipo de salud sin marca, no");
        documentos.Single(d => d.DocumentoId == _aptitudMarcada).SoloConclusionAptitud.Should().BeTrue();
        documentos.Single(d => d.DocumentoId == _formacion).SoloConclusionAptitud.Should().BeFalse();
        (await lector.ArchivoAutorizadoAsync(_contrato.Id, _aptitudMarcada)).Should().NotBeNull("el validador abre el certificado");
        (await lector.ArchivoAutorizadoAsync(_contrato.Id, _informeSinMarca)).Should().BeNull("ni pidiéndolo por su identificador");
    }

    [Fact]
    public async Task Registrar_copia_la_no_medica_al_Tenant_del_Cliente_empresarial_y_de_la_medica_solo_guarda_la_conclusion()
    {
        var noMedica = await RegistrarAsync(_formacion, _tipoFormacionX);
        var medica = await RegistrarAsync(_aptitudMarcada, _tipoAptitudX, ConclusionAptitud.AptoConRestricciones);

        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var copia = await contexto.EvidenciasInbound.SingleAsync(e => e.Id == noMedica);
        copia.TenantId.Should().Be(_lector.Id);
        (copia.TenantOrigenId, copia.DocumentoOrigenId, copia.AutorizacionId, copia.ContratoAccesoId)
            .Should().Be((_contratista.Id, _formacion, _autorizacion, _contrato.Id));
        copia.ArchivoUrl.Should().StartWith($"{_lector.Id:N}/").And.NotBe(_referenciaOriginal);
        copia.HuellaSha256.Should().Be(Convert.ToHexString(SHA256.HashData(ContenidoOriginal)).ToLowerInvariant());
        await using (var abierta = await Almacen(_lector.Id).AbrirAsync(copia.ArchivoUrl!))
        {
            using var memoria = new MemoryStream();
            await abierta.CopyToAsync(memoria);
            memoria.ToArray().Should().Equal(ContenidoOriginal, "la copia se descifra con la clave del Tenant del Cliente empresarial");
        }

        var conclusion = await contexto.EvidenciasInbound.SingleAsync(e => e.Id == medica);
        (conclusion.SoloConclusionAptitud, conclusion.ConclusionAptitud, conclusion.ArchivoUrl, conclusion.HuellaSha256)
            .Should().Be((true, ConclusionAptitud.AptoConRestricciones, (string?)null, (string?)null));
        (conclusion.SujetoNombre, conclusion.SujetoDocumentoIdentidad).Should().Be(("Ana Pérez", "12345678Z"));

        (await AccesosEnElTenantPropietarioAsync()).Where(a => a.Tipo == TipoAccesoLecturaInbound.Apertura)
            .Select(a => a.DocumentoId).Should().Equal([_formacion], "la médica no pidió el archivo del certificado");
        Directory.GetFiles(Path.Combine(_rutaArchivos, _lector.Id.ToString("N"))).Should().ContainSingle("una sola copia: la no médica");
    }

    [Fact]
    public async Task Las_evidencias_de_un_Tenant_no_las_ve_otro()
    {
        var evidencia = await RegistrarAsync(_formacion, _tipoFormacionX);

        await using (var contexto = Contexto(_otro.Id, _usuarioOtro))
        {
            (await contexto.EvidenciasInbound.IgnoreQueryFilters().CountAsync()).Should().Be(0, "RLS, sin el filtro de EF");
            (await contexto.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"EvidenciasInbound\"").SingleAsync())
                .Should().Be(0);
        }

        await using (var contexto = Contexto(_contratista.Id, _usuarioContratista))
            (await contexto.EvidenciasInbound.IgnoreQueryFilters().AnyAsync(e => e.Id == evidencia))
                .Should().BeFalse("tampoco el Tenant de la Empresa contratista ve la copia de su original");

        await using (var contexto = Contexto(_lector.Id, _usuarioLector))
            (await contexto.EvidenciasInbound.IgnoreQueryFilters().CountAsync()).Should().Be(1, "control: su Tenant propietario sí");
    }

    [Fact]
    public async Task El_CHECK_medico_impide_archivo_en_la_de_solo_conclusion_y_conclusion_en_la_otra()
    {
        var medica = await RegistrarAsync(_aptitudMarcada, _tipoAptitudX, ConclusionAptitud.Apto);
        var noMedica = await RegistrarAsync(_formacion, _tipoFormacionX);

        foreach (var (sentencia, motivo) in new[]
                 {
                     ($"UPDATE \"EvidenciasInbound\" SET \"ArchivoUrl\" = 'x/informe.pdf', \"HuellaSha256\" = repeat('a', 64) WHERE \"Id\" = '{medica}'",
                         "una evidencia de solo conclusión con archivo"),
                     ($"UPDATE \"EvidenciasInbound\" SET \"ConclusionAptitud\" = 'Apto' WHERE \"Id\" = '{noMedica}'",
                         "una evidencia con archivo y conclusión"),
                     ($"UPDATE \"EvidenciasInbound\" SET \"ArchivoUrl\" = NULL, \"HuellaSha256\" = NULL WHERE \"Id\" = '{noMedica}'",
                         "una evidencia sin archivo ni conclusión"),
                 })
        {
            var error = await EjecutarComoPropietarioAsync(sentencia);
            error.Should().NotBeNull(motivo);
            (error!.SqlState, error.ConstraintName).Should().Be(
                (PostgresErrorCodes.CheckViolation, "CK_EvidenciasInbound_ArchivoSegunConclusion"), motivo);
        }

        (await EjecutarComoPropietarioAsync(
                $"UPDATE \"EvidenciasInbound\" SET \"ConclusionAptitud\" = 'NoApto' WHERE \"Id\" = '{medica}'"))
            .Should().BeNull("control: la misma identidad sí cambia lo que el CHECK admite");
    }

    [Fact]
    public async Task Una_evidencia_con_archivo_no_puede_ser_de_un_tipo_marcado_del_catalogo_propio()
    {
        var noMedica = await RegistrarAsync(_formacion, _tipoFormacionX);

        var error = await EjecutarComoPropietarioAsync(
            $"UPDATE \"EvidenciasInbound\" SET \"TipoDocumentoId\" = '{_tipoAptitudX}' WHERE \"Id\" = '{noMedica}'");

        error.Should().NotBeNull();
        error!.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        error.MessageText.Should().Contain("solo conclusión de aptitud");
    }

    [Fact]
    public async Task Rechazar_y_validar_funcionan_bajo_runtime_con_los_privilegios_por_columna()
    {
        var evidencia = await RegistrarAsync(_formacion, _tipoFormacionX);

        await using (var contexto = Contexto(_lector.Id, _usuarioLector))
        {
            var (repositorio, contratos, alcance, usuario) = (new EvidenciaInboundRepository(contexto), contexto, Alcance(), Usuario());
            (await new RechazarEvidenciaInboundCommandHandler(repositorio, contratos, alcance, usuario, contexto, TimeProvider.System)
                    .Handle(new(evidencia, CausaRechazoAcreditacion.DatosErroneos, "El nombre no coincide"), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await using (var contexto = Contexto(_lector.Id, _usuarioLector))
        {
            var guardada = await contexto.EvidenciasInbound.SingleAsync(e => e.Id == evidencia);
            (guardada.Estado, guardada.CausaRechazo, guardada.MotivoRechazo, guardada.RechazadaPorUsuarioId)
                .Should().Be((EstadoAcreditacion.Rechazada, CausaRechazoAcreditacion.DatosErroneos, "El nombre no coincide", _usuarioLector));
            (await new ValidarEvidenciaInboundCommandHandler(new EvidenciaInboundRepository(contexto), contexto, Alcance(), Usuario(), contexto, TimeProvider.System)
                    .Handle(new(evidencia), CancellationToken.None))
                .EsExitoso.Should().BeTrue();
        }

        await using (var contexto = Contexto(_lector.Id, _usuarioLector))
            (await contexto.EvidenciasInbound.SingleAsync(e => e.Id == evidencia)).Estado.Should().Be(EstadoAcreditacion.Aceptada);
    }

    [Fact]
    public async Task Tras_revocar_la_autorizacion_las_evidencias_se_conservan_y_la_lectura_da_cero_filas()
    {
        var noMedica = await RegistrarAsync(_formacion, _tipoFormacionX);
        var medica = await RegistrarAsync(_aptitudMarcada, _tipoAptitudX, ConclusionAptitud.Apto);

        await using (var contexto = Contexto(_contratista.Id))
        {
            var autorizacion = await contexto.AutorizacionesLecturaDocumental.SingleAsync(a => a.Id == _autorizacion);
            autorizacion.Revocar(_usuarioContratista, DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = Contexto(_lector.Id, _usuarioLector))
        {
            var lector = new LectorDocumentosInboundEnBase(contexto);
            (await lector.DocumentosAutorizadosAsync(_contrato.Id)).Should().BeEmpty("la revocación corta la lectura");
            (await lector.ArchivoAutorizadoAsync(_contrato.Id, _formacion)).Should().BeNull();

            (await contexto.EvidenciasInbound.Select(e => e.Id).ToListAsync()).Should().BeEquivalentTo([noMedica, medica],
                "las copias son del Tenant del Cliente empresarial y la revocación no las toca");
            var porContrato = await new ObtenerEvidenciasInboundQueryHandler(contexto, contexto, contexto, Alcance(), Usuario())
                .Handle(new(_contrato.Id), CancellationToken.None);
            porContrato.Should().HaveCount(2, "y se siguen consultando sin leer el otro Tenant");
        }

        await using (var contexto = Contexto(_lector.Id, _usuarioLector))
        {
            var otra = await new RegistrarEvidenciaInboundCommandHandler(
                    contexto, contexto, contexto, Alcance(), Usuario(), new LectorDocumentosInboundEnBase(contexto),
                    Almacen(_lector.Id), Almacen(_lector.Id), new EvidenciaInboundRepository(contexto), contexto, TimeProvider.System)
                .Handle(new(_contrato.Id, _formacion, _tipoFormacionX, null), CancellationToken.None);
            otra.Error.Should().Be(EvidenciaInboundErrores.NoEncontrada, "revocada, ya no se registra ninguna copia nueva");
        }
    }

    private async Task<Guid> RegistrarAsync(Guid documento, Guid tipo, ConclusionAptitud? conclusion = null)
    {
        await using var contexto = Contexto(_lector.Id, _usuarioLector);
        var almacen = Almacen(_lector.Id);
        var resultado = await new RegistrarEvidenciaInboundCommandHandler(
                contexto, contexto, contexto, Alcance(), Usuario(), new LectorDocumentosInboundEnBase(contexto), almacen, almacen,
                new EvidenciaInboundRepository(contexto), contexto, TimeProvider.System)
            .Handle(new(_contrato.Id, documento, tipo, conclusion), CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue(resultado.EsExitoso ? null : resultado.Error.Mensaje);
        return resultado.Valor;
    }

    private async Task<PostgresException?> EjecutarComoPropietarioAsync(string sentencia)
    {
        await using var propietario = new NpgsqlConnection(_cadena);
        await propietario.OpenAsync();
        await using var orden = new NpgsqlCommand(sentencia, propietario);
        try
        {
            (await orden.ExecuteNonQueryAsync()).Should().Be(1, $"control: la sentencia encuentra su fila ({sentencia})");
            return null;
        }
        catch (PostgresException error)
        {
            return error;
        }
    }

    private AlcanceDatosServiceFalso Alcance() => new(clienteIds: [_cliente]);

    private CurrentUserServiceFalso Usuario() => new(_usuarioLector, Roles.GestorCae, tenantOrigenId: _lector.Id);

    // Ruta absoluta: el entorno de host no se usa.
    private DiskFileStorageService Almacen(Guid tenantId) =>
        new(Options.Create(new DiskFileStorageServiceOptions { Ruta = _rutaArchivos }), null!,
            new TenantActualAmbiental { TenantId = tenantId }, _proteccion, new AlertaOperativaInerte(),
            NullLogger<DiskFileStorageService>.Instance);

    private async Task<List<AccesoLecturaInbound>> AccesosEnElTenantPropietarioAsync()
    {
        await using var contexto = Contexto(_contratista.Id);
        return await contexto.AccesosLecturaInbound.OrderBy(a => a.MomentoUtc).ToListAsync();
    }

    private static void Cuenta(CaeManagerDbContext contexto, Guid id, Guid tenantId, Guid rolId)
    {
        contexto.Users.Add(new ApplicationUser
        {
            Id = id,
            TenantId = tenantId,
            UserName = $"{id:N}@caemanager.local",
            NormalizedUserName = $"{id:N}@CAEMANAGER.LOCAL",
            Email = $"{id:N}@caemanager.local",
            NormalizedEmail = $"{id:N}@CAEMANAGER.LOCAL",
            NombreCompleto = "Usuario",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString()
        });
        contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = rolId });
    }

    // Sin usuario: identidad propietaria (siembra y lectura de control). Con usuario: cae_app_runtime con contexto firmado.
    private CaeManagerDbContext Contexto(Guid tenantId, Guid? usuario = null)
    {
        ICurrentUserService? usuarioActual = usuario is { } id ? new CurrentUserServiceFalso(id, Roles.GestorCae, tenantOrigenId: tenantId) : null;
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(
            usuarioActual is null ? _cadena : BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena),
            n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        opciones.AddInterceptors(new TenantSelladoInterceptor(tenant));
        if (usuarioActual is not null)
            opciones.AddInterceptors(new TenantRlsConnectionInterceptor(tenant, new SinTenantSeleccionado(),
                usuarioActual, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor());
        return new(opciones.Options, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
