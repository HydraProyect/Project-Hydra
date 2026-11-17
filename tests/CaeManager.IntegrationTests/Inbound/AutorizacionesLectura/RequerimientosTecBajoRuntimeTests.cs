using CaeManager.Domain.Centros;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Domain.Tenants;
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
/// Lectura inversa X-2 de la demo Inbound (P0a): <c>app_inbound_tec_requerimientos()</c> bajo <c>cae_app_runtime</c> con el
/// contexto firmado. El Tenant TEC lee los requisitos que el Tenant TCE le pide en sus contratos de acceso Activos y
/// vinculados (D10), solo de las capas del Centro y de las exigencias del Cliente empresarial (D9), y sin ninguna columna
/// que identifique a un Gestor CAE (P-4, Q-18). Contratos y plantillas comparten Centro y Cliente empresarial, así que lo
/// único que separa un contrato que se lee de uno que no es su estado, su vínculo o la línea Inbound de su Tenant TCE.
/// </summary>
public class RequerimientosTecBajoRuntimeTests : IAsyncLifetime
{
    private const string Funcion = "public.app_inbound_tec_requerimientos()";

    private static readonly AtributosRequisito Basicos = new(
        OrigenRequisito.PropioTenant, ModoVigenciaRequisito.LaPoneElDocumento, VigenciaMeses: null, ToleranciaDias: null,
        BloqueaAcceso: true, Criterio: "Vigente y firmado", DestinatarioRequisito.Todos, TiposContratacion.Todos, [],
        AmbitoDocumentoRequisito.GeneralTec);

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _tce = new("Logística Delta Sur S.A.");
    private readonly Tenant _tceSinLinea = new("Tenant TCE sin línea Inbound");
    private readonly Tenant _tec = new("Montajes Moncayo S.L.");
    private readonly Tenant _otroTec = new("Otro Tenant TEC de prueba");
    private readonly Guid _usuarioTec = Guid.NewGuid();
    private readonly Guid _usuarioOtroTec = Guid.NewGuid();
    private readonly DateOnly _hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    private Guid _activo;
    private Guid _centro;
    private Guid _requisitoCentro;
    private Guid _requisitoN3;
    private Guid _tipoCentro;
    private Guid _tipoN3;
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
            var otroCentro = new Centro(cliente.Id, cliente.Id, "Otro Centro de Delta Sur");
            contexto.Centros.AddRange(centro, otroCentro);
            var tipoCentro = new TipoDocumento("Formación específica del puesto", null, false, 0, AmbitoAplicacion.Trabajador);
            var tipoN3 = new TipoDocumento("Seguro de responsabilidad civil", null, false, 1, AmbitoAplicacion.Empresa);
            var tipoRetirado = new TipoDocumento("Requisito retirado", null, false, 2, AmbitoAplicacion.Trabajador);
            var tipoOtroCentro = new TipoDocumento("Requisito de otro Centro", null, false, 3, AmbitoAplicacion.Trabajador);
            contexto.TiposDocumento.AddRange(tipoCentro, tipoN3, tipoRetirado, tipoOtroCentro);
            await contexto.SaveChangesAsync();

            var deCentro = PlantillaRequisitos.ParaCentro(centro.Id);
            var requisitoCentro = deCentro.AnadirRequisito(tipoCentro, null, Basicos);
            var retirado = deCentro.AnadirRequisito(tipoRetirado, null, Basicos);
            deCentro.RetirarRequisito(retirado.Id);
            var deN3 = PlantillaRequisitos.ParaExigenciasDe(cliente.Id);
            var requisitoN3 = deN3.AnadirRequisito(tipoN3, null, Basicos with
            {
                Origen = OrigenRequisito.ExigenciaN3, ModoVigencia = ModoVigenciaRequisito.Fija, VigenciaMeses = 12,
                BloqueaAcceso = false, Criterio = "Póliza en vigor", AmbitoDocumento = AmbitoDocumentoRequisito.EspecificoTce,
            });
            var deOtroCentro = PlantillaRequisitos.ParaCentro(otroCentro.Id);
            deOtroCentro.AnadirRequisito(tipoOtroCentro, null, Basicos);
            contexto.PlantillasRequisitos.AddRange(deCentro, deN3, deOtroCentro);

            ContratoAcceso Contrato(bool vincular = true, bool pendiente = false)
            {
                var c = ContratoAcceso.Crear(cliente.Id, centro.Id, contratistaLocal.Id, _hoy.AddDays(-10), null, null,
                    pendienteDeConfirmar: pendiente);
                if (vincular) c.VincularTenantContratista(_tec.Id);
                contexto.ContratosAcceso.Add(c);
                return c;
            }

            _activo = Contrato().Id;
            _excluidos["pendiente de confirmar"] = Contrato(pendiente: true).Id;
            var suspendido = Contrato();
            suspendido.Suspender();
            _excluidos["suspendido"] = suspendido.Id;
            var finalizado = Contrato();
            finalizado.Finalizar(_hoy.AddDays(-1));
            _excluidos["finalizado"] = finalizado.Id;
            var eliminado = Contrato();
            eliminado.MarcarComoEliminado(Guid.NewGuid());
            _excluidos["eliminado"] = eliminado.Id;
            _excluidos["sin vincular"] = Contrato(vincular: false).Id;

            contexto.ProductosContratados.Add(LineaInbound());
            await contexto.SaveChangesAsync();

            _centro = centro.Id;
            _requisitoCentro = requisitoCentro.Id;
            _requisitoN3 = requisitoN3.Id;
            _tipoCentro = tipoCentro.Id;
            _tipoN3 = tipoN3.Id;
        }

        // Un Tenant TCE con plantilla y contrato Activo vinculado al mismo Tenant TEC, pero sin la línea Inbound (DN-7).
        await using (var contexto = Contexto(_tceSinLinea.Id))
        {
            var cliente = Empresa.CrearComoCliente("Cliente empresarial sin línea", "B10380210", false, null, null);
            var contratistaLocal = new Empresa("Montajes Moncayo S.L.", "B10380194");
            contexto.Empresas.AddRange(cliente, contratistaLocal);
            var centro = new Centro(cliente.Id, cliente.Id, "Centro sin línea Inbound");
            contexto.Centros.Add(centro);
            var tipo = new TipoDocumento("Formación del puesto sin línea", null, false, 0, AmbitoAplicacion.Trabajador);
            contexto.TiposDocumento.Add(tipo);
            await contexto.SaveChangesAsync();
            var plantilla = PlantillaRequisitos.ParaCentro(centro.Id);
            plantilla.AnadirRequisito(tipo, null, Basicos);
            contexto.PlantillasRequisitos.Add(plantilla);
            var contrato = ContratoAcceso.Crear(cliente.Id, centro.Id, contratistaLocal.Id, _hoy.AddDays(-10), null, null);
            contrato.VincularTenantContratista(_tec.Id);
            contexto.ContratosAcceso.Add(contrato);
            await contexto.SaveChangesAsync();
            _excluidos["Tenant TCE sin línea"] = contrato.Id;
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task El_Tenant_TEC_vinculado_lee_las_capas_del_Centro_y_del_Cliente_empresarial_de_su_contrato_activo()
    {
        var filas = await LeerComoRuntimeAsync(_tec.Id, _usuarioTec);

        filas.Should().HaveCount(2, "un requisito de la plantilla del Centro y otro de las exigencias N3; ni el retirado ni el de otro Centro");
        filas.Should().AllSatisfy(f =>
        {
            f.ContratoAccesoId.Should().Be(_activo);
            f.TenantTceNombre.Should().Be("Logística Delta Sur S.A.");
            f.CentroId.Should().Be(_centro);
            f.CentroNombre.Should().Be("Nave logística de Delta Sur");
            f.ClienteEmpresarialNombre.Should().Be("Cliente empresarial de Delta Sur");
        });

        var centro = filas.Single(f => f.CapaPlantilla == "DeCentro");
        centro.Should().BeEquivalentTo(new
        {
            RequisitoId = _requisitoCentro, TipoDocumentoId = _tipoCentro, TipoDocumentoNombre = "Formación específica del puesto",
            Entidad = "Trabajador", Criterio = "Vigente y firmado", ModoVigencia = "LaPoneElDocumento", VigenciaMeses = (int?)null,
            ToleranciaDias = (int?)null, BloqueaAcceso = true, AmbitoDocumento = "GeneralTec",
        });
        var n3 = filas.Single(f => f.CapaPlantilla == "ExigenciasClienteEmpresarial");
        n3.Should().BeEquivalentTo(new
        {
            RequisitoId = _requisitoN3, TipoDocumentoId = _tipoN3, TipoDocumentoNombre = "Seguro de responsabilidad civil",
            Entidad = "Empresa", Criterio = "Póliza en vigor", ModoVigencia = "Fija", VigenciaMeses = (int?)12,
            BloqueaAcceso = false, AmbitoDocumento = "EspecificoTce",
        });
    }

    [Fact]
    public async Task Un_contrato_que_no_esta_Activo_o_un_Tenant_TCE_sin_linea_Inbound_no_da_filas()
    {
        var contratos = (await LeerComoRuntimeAsync(_tec.Id, _usuarioTec)).Select(f => f.ContratoAccesoId).Distinct().ToList();

        contratos.Should().Equal([_activo], "control: el contrato Activo, con las mismas plantillas, sí se lee");
        foreach (var (caso, contrato) in _excluidos)
            contratos.Should().NotContain(contrato, caso);
        (await RequisitosActivosDelTenantAsync(_tceSinLinea.Id)).Should().BePositive(
            "control: el Tenant TCE sin línea tiene requisitos, y lo que los quita es el gate DN-7");
    }

    [Fact]
    public async Task Otro_Tenant_TEC_no_ve_nada_aunque_la_GUC_diga_el_Tenant_TEC_vinculado()
    {
        (await LeerComoRuntimeAsync(_otroTec.Id, _usuarioOtroTec)).Should().BeEmpty();

        // La conexión puede escribir app.tenant_id, pero no firmar el contexto: el Tenant TEC sale del token.
        (await LeerComoRuntimeAsync(_otroTec.Id, _usuarioOtroTec, $"SET LOCAL app.tenant_id = '{_tec.Id}';")).Should().BeEmpty();
        (await LeerComoRuntimeAsync(_tec.Id, _usuarioTec)).Should().NotBeEmpty("control: con su propio token, el Tenant TEC sí lee");
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

            excepcion.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "«sin contexto» no es «sin requisitos»");
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

        columnas.Should().Equal(
            "ContratoAccesoId", "TenantTceNombre", "CentroId", "CentroNombre", "ClienteEmpresarialNombre", "RequisitoId",
            "TipoDocumentoId", "TipoDocumentoNombre", "Entidad", "Criterio", "ModoVigencia", "VigenciaMeses", "ToleranciaDias",
            "BloqueaAcceso", "AmbitoDocumento", "CapaPlantilla");
        columnas.Should().NotContain(c => c.Contains("Usuario", StringComparison.OrdinalIgnoreCase)
                                          || c.Contains("Gestor", StringComparison.OrdinalIgnoreCase)
                                          || c.Contains("Email", StringComparison.OrdinalIgnoreCase)
                                          || c.Contains("Correo", StringComparison.OrdinalIgnoreCase)
                                          || c.Contains("Ejecutivo", StringComparison.OrdinalIgnoreCase),
            "P-4 y Q-18: ni usuario, ni nombre, ni correo de un Gestor CAE");
    }

    [Fact]
    public async Task Es_SECURITY_DEFINER_con_search_path_fijo_y_solo_la_ejecuta_el_runtime()
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand($"""
            SELECT p.prosecdef, p.proconfig, p.provolatile::text, p.pronargs,
                   p.proowner = (SELECT c.relowner FROM pg_class c WHERE c.oid = 'public."ContratosAcceso"'::regclass),
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
        lector.GetBoolean(7).Should().BeFalse("Soporte TALVEG no lee los requerimientos de un Tenant TEC");
        lector.GetBoolean(8).Should().BeFalse();
    }

    private sealed record Fila(
        Guid ContratoAccesoId, string TenantTceNombre, Guid CentroId, string CentroNombre, string ClienteEmpresarialNombre,
        Guid RequisitoId, Guid TipoDocumentoId, string TipoDocumentoNombre, string Entidad, string? Criterio, string ModoVigencia,
        int? VigenciaMeses, int? ToleranciaDias, bool BloqueaAcceso, string AmbitoDocumento, string CapaPlantilla);

    private async Task<List<Fila>> LeerComoRuntimeAsync(Guid tenant, Guid usuario, string? antes = null)
    {
        await using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena));
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenant, tenantOrigenId: tenant, usuarioId: usuario, local: true,
            transaccion: transaccion);

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
                lector.GetGuid(0), lector.GetString(1), lector.GetGuid(2), lector.GetString(3), lector.GetString(4),
                lector.GetGuid(5), lector.GetGuid(6), lector.GetString(7), lector.GetString(8),
                lector.IsDBNull(9) ? null : lector.GetString(9), lector.GetString(10),
                lector.IsDBNull(11) ? null : lector.GetInt32(11), lector.IsDBNull(12) ? null : lector.GetInt32(12),
                lector.GetBoolean(13), lector.GetString(14), lector.GetString(15)));
        }

        return filas;
    }

    private async Task<long> RequisitosActivosDelTenantAsync(Guid tenant)
    {
        // Identidad propietaria: ve las filas sin la RLS.
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """SELECT count(*) FROM public."RequisitosPlantilla" WHERE "TenantId" = @tenant AND "Activo";""", conexion);
        comando.Parameters.AddWithValue("tenant", tenant);
        return (long)(await comando.ExecuteScalarAsync())!;
    }

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
