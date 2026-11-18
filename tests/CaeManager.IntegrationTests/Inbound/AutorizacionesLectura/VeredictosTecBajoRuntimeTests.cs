using CaeManager.Application.Inbound.AutorizacionesLectura;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Inbound.EvidenciasInbound;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Inbound.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Inbound.AutorizacionesLectura;

/// <summary>
/// Lectura inversa X-2 de la demo Inbound (P0b): <c>app_inbound_tec_veredictos()</c> bajo <c>cae_app_runtime</c> con el
/// contexto firmado. El Tenant TEC lee el veredicto que el Tenant TCE dio sobre sus propios Documentos (evidencias con
/// <c>"TenantOrigenId"</c> = el Tenant TEC del contexto), solo por contratos de acceso que pasan el gate D10 de P0a (Activo,
/// vinculado, dentro de sus fechas y con la línea Inbound vigente del Tenant TCE), sin ninguna identidad de Gestor CAE
/// (P-4) y sin la causa ni el motivo de una evidencia de salud. Todos los contratos comparten Centro, Cliente empresarial y
/// tipo de las evidencias, así que lo único que separa un veredicto que se lee de uno que no es el contrato, su Tenant TCE o
/// el origen de la evidencia.
/// </summary>
public class VeredictosTecBajoRuntimeTests : IAsyncLifetime
{
    private const string Funcion = "public.app_inbound_tec_veredictos()";
    private const string HuellaFicticia = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static readonly DateTime Validada = new(2026, 11, 10, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Rechazada = new(2026, 11, 11, 12, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime Revalidada = new(2026, 11, 12, 8, 45, 0, DateTimeKind.Utc);

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _tce = new("Logística Delta Sur S.A.");
    private readonly Tenant _tceSinLinea = new("Tenant TCE sin línea Inbound");
    private readonly Tenant _tec = new("Montajes Moncayo S.L.");
    private readonly Tenant _otroTec = new("Otro Tenant TEC de prueba");
    private readonly Guid _usuarioTec = Guid.NewGuid();
    private readonly Guid _usuarioOtroTec = Guid.NewGuid();
    private readonly Guid _gestorTce = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();

    private Guid _activo;
    private Guid _docAceptado;
    private Guid _docRechazado;
    private Guid _docRevalidado;
    private readonly Dictionary<string, Guid> _deSalud = [];
    private readonly Dictionary<string, Guid> _excluidos = [];

    public async Task InitializeAsync()
    {
        await using (var contexto = Contexto(_tce.Id))
        {
            await contexto.Database.MigrateAsync();
            contexto.Tenants.AddRange(_tce, _tceSinLinea, _tec, _otroTec);

            var cliente = Empresa.CrearComoCliente("Cliente empresarial de Delta Sur", "B10380186", false, null, null);
            var contratistaLocal = new Empresa("Montajes Moncayo S.L.", "B10380194");
            contexto.Empresas.AddRange(cliente, contratistaLocal);
            var centro = new Centro(cliente.Id, cliente.Id, "Nave logística de Delta Sur");
            contexto.Centros.Add(centro);
            var formacion = new TipoDocumento("Formación específica del puesto", null, false, 0, AmbitoAplicacion.Trabajador,
                sensibilidad: SensibilidadDocumental.DatosPersonales);
            var aptitud = new TipoDocumento("Certificado de aptitud médica", 12, false, 1, AmbitoAplicacion.Trabajador,
                sensibilidad: SensibilidadDocumental.CategoriaEspecialSalud);
            aptitud.EstablecerSoloConclusionAptitud(true);
            var saludSinMarca = new TipoDocumento("Informe de vigilancia de la salud", null, false, 2, AmbitoAplicacion.Trabajador,
                sensibilidad: SensibilidadDocumental.CategoriaEspecialSalud);
            contexto.TiposDocumento.AddRange(formacion, aptitud, saludSinMarca);
            await contexto.SaveChangesAsync();

            ContratoAcceso Contrato(bool vincular = true, bool pendiente = false, DateOnly? inicio = null, DateOnly? fin = null)
            {
                var c = ContratoAcceso.Crear(cliente.Id, centro.Id, contratistaLocal.Id, inicio ?? _hoy.AddDays(-10), fin, null,
                    pendienteDeConfirmar: pendiente);
                if (vincular) c.VincularTenantContratista(_tec.Id);
                contexto.ContratosAcceso.Add(c);
                return c;
            }

            var activo = Contrato();
            var excluidos = new Dictionary<string, ContratoAcceso>
            {
                ["pendiente de confirmar"] = Contrato(pendiente: true),
                ["sin vincular"] = Contrato(vincular: false),
                // Dos días de margen: el día de negocio de la función sale de now() y el del fixture de DiaDeNegocio.Hoy().
                ["fin ya pasado"] = Contrato(inicio: _hoy.AddDays(-30), fin: _hoy.AddDays(-2)),
                ["inicio futuro"] = Contrato(inicio: _hoy.AddDays(2)),
            };
            var suspendido = Contrato();
            suspendido.Suspender();
            excluidos["suspendido"] = suspendido;
            var finalizado = Contrato();
            finalizado.Finalizar(_hoy.AddDays(-1));
            excluidos["finalizado"] = finalizado;
            var eliminado = Contrato();
            eliminado.MarcarComoEliminado(Guid.NewGuid());
            excluidos["contrato eliminado"] = eliminado;
            contexto.ProductosContratados.Add(LineaInbound());
            await contexto.SaveChangesAsync();
            _activo = activo.Id;

            // Los tres veredictos que se leen sin restricción de salud.
            var aceptada = Evidencia(activo.Id, _tec.Id, formacion.Id);
            var rechazada = Evidencia(activo.Id, _tec.Id, formacion.Id);
            rechazada.Rechazar(CausaRechazoEvidenciaInbound.Ilegible, "El escaneo no se lee en la segunda página.", _gestorTce, Rechazada);
            var revalidada = Evidencia(activo.Id, _tec.Id, formacion.Id);
            revalidada.Rechazar(CausaRechazoEvidenciaInbound.FaltaFirmaOSello, "Falta el sello de la entidad formadora.", _gestorTce, Rechazada);
            revalidada.Validar(_gestorTce, Revalidada);
            contexto.EvidenciasInbound.AddRange(aceptada, rechazada, revalidada);
            _docAceptado = aceptada.DocumentoOrigenId;
            _docRechazado = rechazada.DocumentoOrigenId;
            _docRevalidado = revalidada.DocumentoOrigenId;

            // Las de salud, rechazadas con un motivo que no debe salir: por el tipo del Tenant TCE marcado, por un tipo de
            // salud sin la marca y por haber llegado como solo conclusión aunque el tipo elegido no sea de salud.
            var deSalud = new Dictionary<string, EvidenciaInbound>
            {
                ["tipo de solo conclusión"] = Evidencia(activo.Id, _tec.Id, aptitud.Id, soloConclusion: true),
                ["tipo de salud sin la marca"] = Evidencia(activo.Id, _tec.Id, saludSinMarca.Id),
                ["llegó como solo conclusión"] = Evidencia(activo.Id, _tec.Id, formacion.Id, soloConclusion: true),
            };
            foreach (var (caso, evidencia) in deSalud)
            {
                evidencia.Rechazar(CausaRechazoEvidenciaInbound.Otro, "Motivo clínico ficticio que no debe salir.", _gestorTce, Rechazada);
                contexto.EvidenciasInbound.Add(evidencia);
                _deSalud[caso] = evidencia.DocumentoOrigenId;
            }

            // Lo que no se lee: una evidencia dada de baja, una de otro Tenant TEC colgada del contrato vinculado y una propia
            // en cada contrato que no pasa el gate D10.
            var baja = Evidencia(activo.Id, _tec.Id, formacion.Id);
            baja.MarcarComoEliminado(Guid.NewGuid());
            var deOtroOrigen = Evidencia(activo.Id, _otroTec.Id, formacion.Id);
            contexto.EvidenciasInbound.AddRange(baja, deOtroOrigen);
            _excluidos["evidencia eliminada"] = baja.DocumentoOrigenId;
            _excluidos["evidencia de otro Tenant TEC"] = deOtroOrigen.DocumentoOrigenId;
            foreach (var (caso, contrato) in excluidos)
            {
                var evidencia = Evidencia(contrato.Id, _tec.Id, formacion.Id);
                contexto.EvidenciasInbound.Add(evidencia);
                _excluidos[caso] = evidencia.DocumentoOrigenId;
            }

            await contexto.SaveChangesAsync();
        }

        // Un Tenant TCE con contrato Activo vinculado al mismo Tenant TEC y una evidencia suya, pero sin la línea Inbound.
        await using (var contexto = Contexto(_tceSinLinea.Id))
        {
            var cliente = Empresa.CrearComoCliente("Cliente empresarial sin línea", "B10380210", false, null, null);
            var contratistaLocal = new Empresa("Montajes Moncayo S.L.", "B10380194");
            contexto.Empresas.AddRange(cliente, contratistaLocal);
            var centro = new Centro(cliente.Id, cliente.Id, "Centro sin línea Inbound");
            contexto.Centros.Add(centro);
            var tipo = new TipoDocumento("Formación del puesto sin línea", null, false, 0, AmbitoAplicacion.Trabajador,
                sensibilidad: SensibilidadDocumental.DatosPersonales);
            contexto.TiposDocumento.Add(tipo);
            var contrato = ContratoAcceso.Crear(cliente.Id, centro.Id, contratistaLocal.Id, _hoy.AddDays(-10), null, null);
            contrato.VincularTenantContratista(_tec.Id);
            contexto.ContratosAcceso.Add(contrato);
            await contexto.SaveChangesAsync();
            var evidencia = Evidencia(contrato.Id, _tec.Id, tipo.Id);
            contexto.EvidenciasInbound.Add(evidencia);
            await contexto.SaveChangesAsync();
            _excluidos["Tenant TCE sin línea"] = evidencia.DocumentoOrigenId;
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task El_Tenant_TEC_vinculado_lee_el_veredicto_de_sus_Documentos_con_causa_motivo_y_fecha()
    {
        var filas = await LeerComoRuntimeAsync(_tec.Id, _usuarioTec);

        filas.Should().HaveCount(3 + _deSalud.Count);
        filas.Should().OnlyContain(f => f.ContratoAccesoId == _activo);
        filas.Single(f => f.DocumentoOrigenId == _docAceptado).Should().Be(
            new Fila(_activo, _docAceptado, "Aceptada", null, null, Validada));
        filas.Single(f => f.DocumentoOrigenId == _docRechazado).Should().Be(
            new Fila(_activo, _docRechazado, "Rechazada", "Ilegible", "El escaneo no se lee en la segunda página.", Rechazada));
        // Vuelta a validar: el último rechazo sigue en la evidencia como hecho pasado, pero ya no es el veredicto.
        filas.Single(f => f.DocumentoOrigenId == _docRevalidado).Should().Be(
            new Fila(_activo, _docRevalidado, "Aceptada", null, null, Revalidada));
    }

    [Fact]
    public async Task Una_evidencia_de_salud_solo_da_el_estado_y_la_fecha()
    {
        var filas = await LeerComoRuntimeAsync(_tec.Id, _usuarioTec);

        foreach (var (caso, documento) in _deSalud)
            filas.Single(f => f.DocumentoOrigenId == documento).Should().Be(
                new Fila(_activo, documento, "Rechazada", null, null, Rechazada), caso);
        // Control: la base sí guarda la causa y el motivo de esas evidencias; lo que los calla es la función.
        foreach (var (caso, documento) in _deSalud)
            (await CausaYMotivoGuardadosAsync(documento)).Should().Be(("Otro", "Motivo clínico ficticio que no debe salir."), caso);
    }

    [Fact]
    public async Task Sin_contrato_Activo_y_vinculado_sin_linea_Inbound_o_de_otro_origen_no_da_filas()
    {
        var documentos = (await LeerComoRuntimeAsync(_tec.Id, _usuarioTec)).Select(f => f.DocumentoOrigenId).ToList();

        documentos.Should().Contain(_docAceptado, "control: con el mismo tipo, el contrato que pasa el gate sí se lee");
        foreach (var (caso, documento) in _excluidos)
        {
            documentos.Should().NotContain(documento, caso);
            (await EvidenciaExisteAsync(documento)).Should().BeTrue($"control: la evidencia «{caso}» existe y la quita la función");
        }
    }

    [Fact]
    public async Task Otro_Tenant_TEC_no_ve_nada_aunque_la_GUC_diga_el_Tenant_TEC_vinculado()
    {
        (await LeerComoRuntimeAsync(_otroTec.Id, _usuarioOtroTec)).Should().BeEmpty(
            "ni siquiera su evidencia colgada del contrato vinculado a otro Tenant TEC");

        // La conexión puede escribir app.tenant_id, pero no firmar el contexto: el Tenant TEC sale del token.
        (await LeerComoRuntimeAsync(_otroTec.Id, _usuarioOtroTec, $"SET LOCAL app.tenant_id = '{_tec.Id}';")).Should().BeEmpty();
        (await LeerComoRuntimeAsync(_tec.Id, _usuarioTec)).Should().NotBeEmpty("control: con su propio token, el Tenant TEC sí lee");
    }

    [Fact]
    public async Task Bajo_un_Operador_CAE_externo_solo_cuenta_el_Tenant_activo_y_nunca_el_de_origen()
    {
        (await LeerComoRuntimeAsync(_otroTec.Id, _usuarioTec, tenantOrigen: _tec.Id)).Should().BeEmpty(
            "el Tenant TEC es el activo del contexto, no el de origen de la persona (operación ≠ propiedad)");
        (await LeerComoRuntimeAsync(_tec.Id, _usuarioOtroTec, tenantOrigen: _otroTec.Id)).Should().HaveCount(3 + _deSalud.Count);
    }

    [Fact]
    public async Task Sin_contexto_firmado_o_sin_usuario_lanza_42501()
    {
        foreach (var firmar in new Func<NpgsqlConnection, NpgsqlTransaction, Task>[]
                 {
                     (_, _) => Task.CompletedTask,
                     (conexion, transaccion) => ContextoRlsDePruebas.FijarAsync(conexion, tenantId: _tec.Id, local: true, transaccion: transaccion),
                 })
        {
            await using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena));
            await conexion.OpenAsync();
            await using var transaccion = await conexion.BeginTransactionAsync();
            await firmar(conexion, transaccion);
            await using var comando = new NpgsqlCommand($"SELECT * FROM {Funcion};", conexion, transaccion);

            var excepcion = await Assert.ThrowsAsync<PostgresException>(() => comando.ExecuteNonQueryAsync());

            excepcion.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "«sin contexto» no es «sin veredictos»");
        }
    }

    [Fact]
    public async Task Ninguna_columna_del_resultado_identifica_a_un_Gestor_CAE()
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            $"""
            SELECT a.nombre
              FROM pg_proc p, unnest(p.proargnames, p.proargmodes) WITH ORDINALITY AS a(nombre, modo, orden)
             WHERE p.oid = '{Funcion}'::regprocedure AND a.modo = 't'
             ORDER BY a.orden;
            """, conexion);
        var columnas = new List<string>();
        await using (var lector = await comando.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
                columnas.Add(lector.GetString(0));
        }

        columnas.Should().Equal("ContratoAccesoId", "DocumentoOrigenId", "Estado", "CausaRechazo", "MotivoRechazo", "FechaVeredictoUtc");
        columnas.Should().NotContain(c => c.Contains("Usuario", StringComparison.OrdinalIgnoreCase)
                                          || c.Contains("Gestor", StringComparison.OrdinalIgnoreCase)
                                          || c.Contains("Conclusion", StringComparison.OrdinalIgnoreCase),
            "P-4: ni quién validó ni quién rechazó; y ninguna conclusión de aptitud");
    }

    [Fact]
    public async Task Es_SECURITY_DEFINER_con_search_path_fijo_y_solo_la_ejecuta_el_runtime()
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand($"""
            SELECT p.prosecdef, p.proconfig, p.provolatile::text, p.pronargs,
                   p.proowner = (SELECT c.relowner FROM pg_class c WHERE c.oid = 'public."EvidenciasInbound"'::regclass),
                   COALESCE((SELECT bool_or(a.grantee = 0) FROM aclexplode(p.proacl) a), true),
                   has_function_privilege('cae_app_runtime', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_soporte', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_aprovisionamiento', p.oid, 'EXECUTE')
            FROM pg_proc p WHERE p.oid = '{Funcion}'::regprocedure;
            """, conexion);

        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        lector.GetBoolean(0).Should().BeTrue("sin SECURITY DEFINER no leería nada del Tenant TCE");
        lector.GetFieldValue<string[]>(1).Should().Equal(["search_path=pg_catalog, pg_temp"]);
        lector.GetString(2).Should().Be("v", "compara el inicio de la línea con clock_timestamp(), como el gate DN-7");
        lector.GetInt16(3).Should().Be(0, "ningún parámetro elige el Tenant TEC");
        lector.GetBoolean(4).Should().BeTrue("lee con el propietario de las tablas");
        lector.GetBoolean(5).Should().BeFalse("PUBLIC no la ejecuta");
        lector.GetBoolean(6).Should().BeTrue();
        lector.GetBoolean(7).Should().BeFalse("Soporte TALVEG no lee los veredictos de un Tenant TEC");
        lector.GetBoolean(8).Should().BeFalse();
    }

    [Fact]
    public async Task El_lector_de_Infrastructure_traduce_las_filas_de_la_funcion()
    {
        await using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena));
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: _tec.Id, tenantOrigenId: _tec.Id, usuarioId: _usuarioTec,
            local: true, transaccion: transaccion);
        var tenant = new TenantActualAmbiental { TenantId = _tec.Id };
        await using var contexto = new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(conexion).Options, new EphemeralDataProtectionProvider(), tenant);
        await contexto.Database.UseTransactionAsync(transaccion);

        var veredictos = await new LectorVeredictosTecEnBase(contexto).VeredictosAsync();

        veredictos.Should().HaveCount(3 + _deSalud.Count);
        veredictos.Single(v => v.DocumentoOrigenId == _docRechazado).Should().Be(new VeredictoTecDto(
            _activo, _docRechazado, EstadoVeredictoTec.Rechazada, CausaRechazoEvidenciaInbound.Ilegible,
            "El escaneo no se lee en la segunda página.", Rechazada));
        veredictos.Single(v => v.DocumentoOrigenId == _docAceptado).Should().Be(new VeredictoTecDto(
            _activo, _docAceptado, EstadoVeredictoTec.Aceptada, null, null, Validada));
        veredictos.Where(v => _deSalud.ContainsValue(v.DocumentoOrigenId)).Should().OnlyContain(
            v => v.Estado == EstadoVeredictoTec.Rechazada && v.CausaRechazo == null && v.MotivoRechazo == null);
    }

    private sealed record Fila(
        Guid ContratoAccesoId, Guid DocumentoOrigenId, string Estado, string? CausaRechazo, string? MotivoRechazo, DateTime FechaVeredictoUtc);

    private async Task<List<Fila>> LeerComoRuntimeAsync(Guid tenant, Guid usuario, string? antes = null, Guid? tenantOrigen = null)
    {
        await using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena));
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenant, tenantOrigenId: tenantOrigen ?? tenant, usuarioId: usuario,
            local: true, transaccion: transaccion);

        await using (var identidad = new NpgsqlCommand("SELECT session_user::text || '/' || current_user::text;", conexion, transaccion))
            ((string)(await identidad.ExecuteScalarAsync())!).Should().Be("cae_app_runtime/cae_app_runtime");
        if (antes is not null)
        {
            await using var previo = new NpgsqlCommand(antes, conexion, transaccion);
            await previo.ExecuteNonQueryAsync();
        }

        await using var comando = new NpgsqlCommand($"SELECT * FROM {Funcion};", conexion, transaccion);
        var filas = new List<Fila>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            filas.Add(new Fila(
                lector.GetGuid(0), lector.GetGuid(1), lector.GetString(2),
                lector.IsDBNull(3) ? null : lector.GetString(3), lector.IsDBNull(4) ? null : lector.GetString(4),
                lector.GetFieldValue<DateTime>(5)));
        }

        return filas;
    }

    private async Task<(string? Causa, string? Motivo)> CausaYMotivoGuardadosAsync(Guid documentoOrigen)
    {
        // Identidad propietaria: ve las filas sin la RLS.
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """SELECT "CausaRechazo", "MotivoRechazo" FROM public."EvidenciasInbound" WHERE "DocumentoOrigenId" = @documento;""", conexion);
        comando.Parameters.AddWithValue("documento", documentoOrigen);
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        return (lector.IsDBNull(0) ? null : lector.GetString(0), lector.IsDBNull(1) ? null : lector.GetString(1));
    }

    private async Task<bool> EvidenciaExisteAsync(Guid documentoOrigen)
    {
        // Identidad propietaria: ve las filas sin la RLS.
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """SELECT count(*) FROM public."EvidenciasInbound" WHERE "DocumentoOrigenId" = @documento;""", conexion);
        comando.Parameters.AddWithValue("documento", documentoOrigen);
        return (long)(await comando.ExecuteScalarAsync())! == 1;
    }

    // Cada evidencia con su propio Documento de origen (sin FK: es un identificador del Tenant TEC) y su propia autorización.
    private EvidenciaInbound Evidencia(Guid contrato, Guid tenantOrigen, Guid tipo, bool soloConclusion = false) =>
        EvidenciaInbound.Registrar(
            new EvidenciaInbound.Procedencia(contrato, tenantOrigen, Guid.NewGuid(), Guid.NewGuid()), tipo,
            new EvidenciaInbound.Sujeto(Guid.NewGuid(), "Trabajador ficticio de Montajes Moncayo", "00000000T"),
            _hoy.AddDays(-20), _hoy.AddDays(300), soloConclusion,
            soloConclusion ? ConclusionAptitud.NoApto : null,
            soloConclusion ? null : new EvidenciaInbound.ArchivoCopiado($"{_tce.Id:N}/evidencias/{Guid.NewGuid():N}.pdf", HuellaFicticia),
            _gestorTce, Validada);

    private static ProductoContratado LineaInbound() => ProductoContratado.Contratar(LineaProductoContratado.Inbound,
        "cus_PagadorDeltaSur", TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Anual, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);

    private CaeManagerDbContext Contexto(Guid tenantId)
    {
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenant))
            .Options;
        return new(opciones, new EphemeralDataProtectionProvider(), tenant);
    }
}
