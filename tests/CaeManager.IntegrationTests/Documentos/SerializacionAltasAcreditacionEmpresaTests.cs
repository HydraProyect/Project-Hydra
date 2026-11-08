using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// La carrera de la regla A-1 con dos conexiones reales como <c>cae_app_runtime</c> (revisión Codex de la PR #2074, ronda
/// 2, hallazgo 1): el alta de un Documento de Empresa y la de un acceso de plataforma de un Centro de su Empresa, cada una
/// leyendo antes de que la otra confirme. Sin serializar, ninguna ve a la otra y el par queda sin crear (el control de que
/// la carrera ocurre y el test la detecta). Con <see cref="SerializacionAltasAcreditacionEmpresa"/>, la segunda espera al
/// candado del Tenant (se comprueba en <c>pg_locks</c> que llegó a esperar), lee después del COMMIT de la primera y crea
/// el par.
/// </summary>
public class SerializacionAltasAcreditacionEmpresaTests
{
    private static readonly DateOnly Emision = new(2026, 1, 15);
    private static readonly TimeSpan Plazo = TimeSpan.FromSeconds(30);

    private readonly TenantActualAmbiental _tenant = new() { TenantId = Guid.NewGuid() };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Las_altas_simultaneas_de_un_documento_de_empresa_y_un_acceso_de_su_centro_no_pierden_el_par(bool serializadas)
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false, tenantActualPersonalizado: _tenant);
        var escenario = await SembrarAsync(arnes);

        await using var ambitoA = arnes.Servicios.CreateAsyncScope();
        await using var ambitoB = arnes.Servicios.CreateAsyncScope();
        var contextoA = ambitoA.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var contextoB = ambitoB.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        (await contextoA.Database.SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync())
            .Should().Be("cae_app_runtime", "el candado se toma con la identidad del tráfico, sin privilegios añadidos");

        var documento = Documento.DeEmpresa(
            escenario.EmpresaId, escenario.TipoId, Emision, VigenciaDocumento.VenceEl(Emision.AddYears(5)));
        var canal = CanalGestionDocumental.DePlataforma(
            escenario.CentroId, "Plataforma", escenario.ProveedorId, "https://plataforma.test", "usuario", "clave");

        var aHaLeido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var soltarA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // A: el alta del Documento de Empresa. Lee (aún no hay acceso) y se detiene antes de guardar.
        async Task<Result> AltaDocumentoAsync(CancellationToken ct)
        {
            contextoA.Documentos.Add(documento);
            await AltaAcreditacionesDePrueba.Con(contextoA)
                .AgregarPendientesAsync(new AltasConAcreditacion { Documentos = [documento] }, ct);
            aHaLeido.TrySetResult();
            await soltarA.Task.WaitAsync(Plazo, ct);
            await contextoA.SaveChangesAsync(ct);
            return Result.Exito();
        }

        // B: el alta del acceso de plataforma del Centro de la misma Empresa.
        async Task<Result> AltaAccesoAsync(CancellationToken ct)
        {
            contextoB.CanalesGestionDocumental.Add(canal);
            await AltaAcreditacionesDePrueba.Con(contextoB)
                .AgregarPendientesAsync(new AltasConAcreditacion { Canales = [canal] }, ct);
            await contextoB.SaveChangesAsync(ct);
            return Result.Exito();
        }

        var tareaA = serializadas
            ? Serializacion(contextoA).EjecutarAsync(AltaDocumentoAsync)
            : AltaDocumentoAsync(CancellationToken.None);
        await aHaLeido.Task.WaitAsync(Plazo);

        if (serializadas)
        {
            // La conexión de B, abierta a mano para que EF no la suelte al pool: su PID es el que espera el candado.
            await contextoB.Database.OpenConnectionAsync();
            var pidB = await contextoB.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
            var tareaB = Serializacion(contextoB).EjecutarAsync(AltaAccesoAsync);

            // Control de la carrera: B ha llegado al candado del Tenant mientras A lo tiene, y espera.
            await EsperarCandadoEnEsperaAsync(arnes.CadenaPropietario, pidB, ClaveDelCandado);
            tareaB.IsCompleted.Should().BeFalse("B no puede leer hasta que A confirme");

            soltarA.TrySetResult();
            (await tareaA.WaitAsync(Plazo)).EsExitoso.Should().BeTrue();
            (await tareaB.WaitAsync(Plazo)).EsExitoso.Should().BeTrue();
        }
        else
        {
            // Sin serializar: B lee y confirma mientras A aún no ha confirmado; después confirma A.
            (await AltaAccesoAsync(CancellationToken.None)).EsExitoso.Should().BeTrue();
            soltarA.TrySetResult();
            (await tareaA.WaitAsync(Plazo)).EsExitoso.Should().BeTrue();
        }

        var pares = await LeerParesComoPropietarioAsync(arnes, documento.Id);
        if (serializadas)
            pares.Should().BeEquivalentTo([canal.Id], "quien llega segundo ve lo que el primero confirmó y crea el par");
        else
            pares.Should().BeEmpty("control: sin serializar, la carrera deja el par sin crear y el test lo detecta");
    }

    [Fact]
    public async Task Dentro_de_una_transaccion_ya_abierta_toma_el_candado_en_ella()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false, tenantActualPersonalizado: _tenant);
        await using var ambito = arnes.Servicios.CreateAsyncScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        // Abierta a mano: la comprobación posterior al COMMIT mira la misma sesión, no otra conexión del pool.
        await contexto.Database.OpenConnectionAsync();

        var resultado = await new TransaccionDeComando(contexto).EjecutarAsync(async ct =>
        {
            var interior = await Serializacion(contexto).EjecutarAsync(async c =>
            {
                (await CandadosDelTenantConcedidosEnEstaSesionAsync(contexto, c)).Should().Be(1,
                    "el candado del Tenant se toma en la transacción ya abierta");
                return Result.Exito();
            }, ct);
            return interior;
        });

        resultado.EsExitoso.Should().BeTrue();
        (await CandadosDelTenantConcedidosEnEstaSesionAsync(contexto, CancellationToken.None)).Should().Be(0,
            "un candado de transacción se suelta con el COMMIT");
    }

    private SerializacionAltasAcreditacionEmpresa Serializacion(CaeManagerDbContext contexto) =>
        new(contexto, new TransaccionDeComando(contexto), _tenant);

    /// <summary>La misma clave que <see cref="SerializacionAltasAcreditacionEmpresa"/>: espacio propio más el Tenant.</summary>
    private string ClaveDelCandado => "app.altas_acreditacion_empresa:" + _tenant.TenantId;

    /// <summary>
    /// Un candado de asesoramiento con clave bigint aparece en <c>pg_locks</c> partido en <c>classid</c> (32 bits altos)
    /// y <c>objid</c> (32 bits bajos), con <c>objsubid = 1</c>; se recompone para compararlo con la clave esperada.
    /// </summary>
    private const string EsElCandadoDeLaClave =
        "locktype = 'advisory' AND objsubid = 1 AND ((classid::bigint << 32) | objid::bigint) = hashtextextended(@clave, 0)";

    private const string CandadosDelTenantConcedidosEnEstaSesion =
        "SELECT count(*)::int AS \"Value\" FROM pg_locks WHERE locktype = 'advisory' AND objsubid = 1 " +
        "AND ((classid::bigint << 32) | objid::bigint) = hashtextextended({0}, 0) AND granted AND pid = pg_backend_pid()";

    private Task<int> CandadosDelTenantConcedidosEnEstaSesionAsync(CaeManagerDbContext contexto, CancellationToken ct) =>
        contexto.Database.SqlQueryRaw<int>(CandadosDelTenantConcedidosEnEstaSesion, ClaveDelCandado).SingleAsync(ct);

    private static async Task EsperarCandadoEnEsperaAsync(string cadenaPropietario, int pid, string clave)
    {
        await using var conexion = new NpgsqlConnection(cadenaPropietario);
        await conexion.OpenAsync();
        var limite = DateTime.UtcNow + Plazo;
        while (DateTime.UtcNow < limite)
        {
            await using var comando = new NpgsqlCommand(
                $"SELECT count(*)::int FROM pg_locks WHERE {EsElCandadoDeLaClave} AND NOT granted AND pid = @pid", conexion);
            comando.Parameters.AddWithValue("clave", clave);
            comando.Parameters.AddWithValue("pid", pid);
            if ((int)(await comando.ExecuteScalarAsync())! > 0)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException("B no llegó a esperar el candado del Tenant: la carrera no se produjo.");
    }

    private static async Task<List<Guid>> LeerParesComoPropietarioAsync(ArnesDeArranqueRuntime arnes, Guid documentoId)
    {
        await using var conexion = new NpgsqlConnection(arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """SELECT "CanalGestionDocumentalId" FROM "AcreditacionesDocumentoPlataforma" WHERE "DocumentoId" = @documento""",
            conexion);
        comando.Parameters.AddWithValue("documento", documentoId);
        var pares = new List<Guid>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            pares.Add(lector.GetGuid(0));
        return pares;
    }

    private sealed record Escenario(Guid EmpresaId, Guid TipoId, Guid CentroId, Guid ProveedorId);

    /// <summary>Una Empresa con un Centro suyo sin accesos ni Trabajadores, y un tipo de Empresa obligatorio por defecto.</summary>
    private static async Task<Escenario> SembrarAsync(ArnesDeArranqueRuntime arnes)
    {
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var cliente = Empresa.CrearComoCliente("Cliente Carrera A1 S.L.", "B10380194", false, null, null);
        var empresa = new Empresa("Empresa Carrera A1 S.L.", "B10380186");
        contexto.Empresas.AddRange(cliente, empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Planta sin accesos");
        contexto.Centros.Add(centro);
        var tipo = new TipoDocumento("Seguro de responsabilidad civil", 12, aplicaVencimientoAutomatico: true, orden: 1,
            AmbitoAplicacion.Empresa, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
        return new Escenario(empresa.Id, tipo.Id, centro.Id, proveedor.Id);
    }
}
