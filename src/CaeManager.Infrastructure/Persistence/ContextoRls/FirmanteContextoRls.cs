using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.ContextoRls;

/// <summary>
/// Firma el contexto de sesión RLS de cada conexión (P6, diseño
/// <c>tecnico/DISENO-CONTEXTO-RLS-FIRMADO-P6-2026-09-23.md</c>).
///
/// <para>
/// <b>La clave.</b> Efímera y por proceso: 32 bytes de
/// <see cref="RandomNumberGenerator"/> que no salen nunca de esta instancia ni
/// de <c>app_privado.claves_contexto</c>. Se registra con la cadena
/// <b>propietaria</b> (<c>ConnectionStrings:CaeManagerDb</c>, la misma que ya
/// usa <see cref="FabricaContextoDeBootstrap"/>), porque es la única identidad
/// que puede escribir en ese esquema: <c>cae_app_runtime</c> no tiene ningún
/// permiso sobre él, así que con su credencial sola no se puede ni leer la
/// clave ni añadir una propia. No hay secreto nuevo en ningún entorno.
/// </para>
///
/// <para>
/// <b>Rotación perezosa.</b> Al firmar, si la clave vigente tiene más de
/// <see cref="Rotacion"/>, se registra otra y se borran las caducadas. No es un
/// servicio de fondo porque el arranque firma conexiones (seeders) antes de que
/// el host levante los servicios. Cada clave vale en la base
/// <see cref="Rotacion"/> + <see cref="Ttl"/> + margen: un token firmado justo
/// antes de rotar sigue validando hasta su propia caducidad.
/// </para>
///
/// <para>
/// <b>Una clave por base de datos</b>, no por proceso: la tabla de claves vive
/// en la base que valida. El registro usa la cadena propietaria con la base de
/// la conexión de tráfico; en producción son la misma, y en los tests de
/// integración (una base por test) cada base recibe la suya.
/// </para>
/// </summary>
public sealed class FirmanteContextoRls
{
    public static readonly TimeSpan TtlPorDefecto = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan RotacionPorDefecto = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan MargenVigenciaClave = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Estado por conexión física abierta: el contexto de base (el que fijó el
    /// interceptor al abrir), el vigente (distinto de la base solo mientras el
    /// sellado lo cambia) y cuándo se firmó por última vez. Estático y débil:
    /// muere con la conexión, y lo comparten el interceptor de conexión y el
    /// de sellado, que no se conocen entre sí.
    /// </summary>
    private static readonly ConditionalWeakTable<DbConnection, EstadoConexion> EstadoPorConexion = new();

    private readonly string _cadenaPropietaria;
    private readonly TimeProvider _reloj;
    private readonly ConcurrentDictionary<string, ClaveVigente> _clavePorBase = new();
    private readonly SemaphoreSlim _registro = new(1, 1);

    public TimeSpan Ttl { get; }
    public TimeSpan Rotacion { get; }

    public FirmanteContextoRls(IConfiguration configuration, TimeProvider reloj)
        : this(
            configuration.GetConnectionString("CaeManagerDb")
                ?? throw new InvalidOperationException(
                    "Falta ConnectionStrings:CaeManagerDb. El contexto RLS firmado registra su clave con la " +
                    "identidad propietaria; sin ella ninguna conexión de tráfico puede llevar contexto."),
            reloj, TtlPorDefecto, RotacionPorDefecto)
    {
    }

    public FirmanteContextoRls(string cadenaPropietaria, TimeProvider reloj, TimeSpan ttl, TimeSpan rotacion)
    {
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        if (rotacion <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(rotacion));
        _cadenaPropietaria = cadenaPropietaria;
        _reloj = reloj;
        Ttl = ttl;
        Rotacion = rotacion;
    }

    /// <summary>
    /// Firma <paramref name="contexto"/> para esta conexión (ya abierta: el
    /// token se ata a su <c>ProcessID</c>). El llamante escribe
    /// <see cref="TokenPendiente.Token"/> en <c>app.contexto</c> y solo después
    /// invoca <see cref="TokenPendiente.Confirmar"/>, que lo recuerda como
    /// contexto de base de la conexión. Si el <c>set_config</c> falla o se
    /// cancela, la conexión se queda sin estado en memoria, igual que en la
    /// base, y renovar o sellar no tienen nada que tocar (hallazgo P2 de Codex,
    /// ronda 2).
    /// </summary>
    public async Task<TokenPendiente> FirmarAsync(NpgsqlConnection conexion, ContextoSesionRls contexto, CancellationToken ct)
    {
        var token = await ConstruirAsync(conexion, contexto, ct);
        var firmadoEn = _reloj.GetUtcNow();
        return new TokenPendiente(token, () =>
            EstadoPorConexion.AddOrUpdate(conexion, new EstadoConexion(this, contexto, contexto, firmadoEn)));
    }

    /// <summary>
    /// Para <c>TenantSelladoInterceptor</c>: vuelve a firmar el contexto de esta
    /// conexión con otro Tenant activo. Si <paramref name="tenantId"/> es el de
    /// la base, restaura la base tal cual. <c>null</c> si la conexión no la
    /// abrió <c>TenantRlsConnectionInterceptor</c> (no hay contexto que tocar).
    ///
    /// <para>
    /// El estado en memoria NO cambia aquí: el llamante invoca
    /// <see cref="TokenPendiente.Confirmar"/> solo después de que el
    /// <c>set_config</c> haya llegado a la base. Si fallara o se cancelara, la
    /// memoria seguiría describiendo el token que la conexión tiene de verdad
    /// (hallazgo P2 de Codex, ronda 1).
    /// </para>
    /// </summary>
    public static async Task<TokenPendiente?> FirmarConTenantAsync(DbConnection conexion, Guid? tenantId, CancellationToken ct)
    {
        if (conexion is not NpgsqlConnection npgsql || !EstadoPorConexion.TryGetValue(conexion, out var estado))
            return null;

        var nuevo = tenantId == estado.Base.TenantId
            ? estado.Base
            : estado.Base with { TenantId = tenantId, Origen = OrigenContextoRls.Sellado };
        var firmadoEn = estado.Firmante._reloj.GetUtcNow();
        var token = await estado.Firmante.ConstruirAsync(npgsql, nuevo, ct);
        return new TokenPendiente(token, () =>
            EstadoPorConexion.AddOrUpdate(conexion, estado with { Vigente = nuevo, FirmadoEn = firmadoEn }));
    }

    /// <summary>
    /// Token nuevo con el mismo contexto vigente si el actual tiene más de la
    /// mitad del TTL; <c>null</c> si no hace falta renovar o la conexión no
    /// tiene contexto firmado. Para conexiones retenidas mucho tiempo abiertas.
    /// Como en <see cref="FirmarConTenantAsync"/>, la renovación solo se
    /// anota al <see cref="TokenPendiente.Confirmar"/>: si el
    /// <c>set_config</c> falla, el siguiente comando lo vuelve a intentar en
    /// vez de dar por renovado un token que la base no tiene.
    /// </summary>
    public static async Task<TokenPendiente?> RenovarSiHaceFaltaAsync(DbConnection conexion, CancellationToken ct)
    {
        if (conexion is not NpgsqlConnection npgsql || !EstadoPorConexion.TryGetValue(conexion, out var estado))
            return null;

        var ahora = estado.Firmante._reloj.GetUtcNow();
        if (ahora - estado.FirmadoEn < estado.Firmante.Ttl / 2)
            return null;

        var token = await estado.Firmante.ConstruirAsync(npgsql, estado.Vigente, ct);
        return new TokenPendiente(token, () =>
            EstadoPorConexion.AddOrUpdate(conexion, estado with { FirmadoEn = ahora }));
    }

    /// <summary>
    /// Token ya firmado que el llamante escribe en <c>app.contexto</c>;
    /// <see cref="Confirmar"/> anota en memoria que la conexión lo tiene, y
    /// solo se llama cuando el <c>set_config</c> terminó bien.
    /// </summary>
    public sealed class TokenPendiente(string token, Action confirmar)
    {
        public string Token { get; } = token;

        public void Confirmar() => confirmar();
    }

    /// <summary>Olvida el contexto de la conexión (se llama al cerrarla).</summary>
    public static void Olvidar(DbConnection conexion) => EstadoPorConexion.Remove(conexion);

    private async Task<string> ConstruirAsync(NpgsqlConnection conexion, ContextoSesionRls contexto, CancellationToken ct)
    {
        var clave = await ClaveParaAsync(conexion, ct);
        return TokenContextoRls.Construir(
            clave.Id, clave.Bytes, contexto, conexion.ProcessID,
            _reloj.GetUtcNow() + Ttl, RandomNumberGenerator.GetHexString(32, lowercase: true));
    }

    private async Task<ClaveVigente> ClaveParaAsync(NpgsqlConnection conexion, CancellationToken ct)
    {
        var baseDeDatos = $"{conexion.Host}:{conexion.Port}/{conexion.Database}";
        if (_clavePorBase.TryGetValue(baseDeDatos, out var clave) && !DebeRotar(clave))
            return clave;

        await _registro.WaitAsync(ct);
        try
        {
            if (_clavePorBase.TryGetValue(baseDeDatos, out clave) && !DebeRotar(clave))
                return clave;

            clave = await RegistrarAsync(conexion.Database, ct);
            _clavePorBase[baseDeDatos] = clave;
            return clave;
        }
        finally
        {
            _registro.Release();
        }
    }

    private bool DebeRotar(ClaveVigente clave) => _reloj.GetUtcNow() - clave.RegistradaEn >= Rotacion;

    private async Task<ClaveVigente> RegistrarAsync(string? baseDeDatos, CancellationToken ct)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var (ipad, opad) = TokenContextoRls.Rellenos(bytes);
        var id = Guid.NewGuid();
        var registradaEn = _reloj.GetUtcNow();

        var cadena = new NpgsqlConnectionStringBuilder(_cadenaPropietaria);
        if (!string.IsNullOrEmpty(baseDeDatos))
            cadena.Database = baseDeDatos;

        // Conexión propietaria cruda, al margen de EF y de los interceptores a
        // propósito (ver ConexionesFueraDelInterceptorTests): no lee ni escribe
        // filas de ningún Tenant, solo la tabla de claves, a la que ningún rol
        // sometido a RLS tiene acceso.
        await using var conexionPropietaria = new NpgsqlConnection(cadena.ConnectionString);
        await conexionPropietaria.OpenAsync(ct);
        await using var comando = conexionPropietaria.CreateCommand();
        comando.CommandText =
            "INSERT INTO app_privado.claves_contexto (id, ipad, opad, valida_hasta) " +
            "VALUES (@id, @ipad, @opad, now() + @vigencia); " +
            "DELETE FROM app_privado.claves_contexto WHERE valida_hasta < now();";
        comando.Parameters.AddWithValue("id", id);
        comando.Parameters.AddWithValue("ipad", ipad);
        comando.Parameters.AddWithValue("opad", opad);
        comando.Parameters.AddWithValue("vigencia", Rotacion + Ttl + MargenVigenciaClave);
        await comando.ExecuteNonQueryAsync(ct);

        return new ClaveVigente(id, bytes, registradaEn);
    }

    private sealed record ClaveVigente(Guid Id, byte[] Bytes, DateTimeOffset RegistradaEn);

    private sealed record EstadoConexion(
        FirmanteContextoRls Firmante, ContextoSesionRls Base, ContextoSesionRls Vigente, DateTimeOffset FirmadoEn);
}
