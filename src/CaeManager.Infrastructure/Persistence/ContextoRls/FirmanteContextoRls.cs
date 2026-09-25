using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CaeManager.Infrastructure.Persistence.ContextoRls;

/// <summary>
/// Firma el contexto de sesión RLS de cada conexión (P6, diseño
/// <c>tecnico/DISENO-CONTEXTO-RLS-FIRMADO-P6-2026-09-23.md</c>).
///
/// <para>
/// <b>La clave.</b> Este proceso no la registra: la registra el migrador con la
/// identidad propietaria y la guarda cifrada con DataProtection
/// (<see cref="ClaveContextoRls"/>). Aquí se lee por la propia conexión de
/// tráfico con <c>app_claves_contexto_protegidas()</c>, se descifra con el
/// anillo del disco y se guarda en memoria por base de datos (host, puerto y
/// base de la conexión de tráfico). Se vuelve a leer cada <see cref="Rotacion"/>,
/// o antes si le queda menos de <see cref="Ttl"/> de vigencia, para recoger la
/// que registre el siguiente despliegue.
/// </para>
///
/// <para>
/// <b>Falla cerrado.</b> Si no hay clave vigente o ninguna se deja descifrar,
/// no se firma: el token es la cadena vacía, <c>app.contexto</c> queda vacío y
/// las funciones <c>app_ctx_*()</c> devuelven NULL. Se registra como error y
/// <c>/salud</c> lo muestra (<c>ClaveContextoRlsHealthCheck</c>). En la fase
/// «expandir» eso no corta el tráfico, porque ninguna política lee todavía el
/// contexto firmado; en «contraer» dejará esa conexión sin filas.
/// </para>
///
/// <para>
/// <b>Registro de respaldo solo con cadena propietaria.</b> Si el proceso tiene
/// <c>ConnectionStrings:CaeManagerDb</c> (desarrollo y tests de integración, con
/// una base por test que ningún migrador ha preparado) y no encuentra clave, la
/// registra él. En staging y producción esa cadena está vacía en el contenedor
/// <c>app</c> (#882), así que la vía no existe.
/// </para>
/// </summary>
public sealed class FirmanteContextoRls
{
    public static readonly TimeSpan TtlPorDefecto = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan RotacionPorDefecto = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Estado por conexión física abierta: el contexto de base (el que fijó el
    /// interceptor al abrir), el vigente (distinto de la base solo mientras el
    /// sellado lo cambia) y cuándo se firmó por última vez. Estático y débil:
    /// muere con la conexión, y lo comparten el interceptor de conexión y el
    /// de sellado, que no se conocen entre sí.
    /// </summary>
    private static readonly ConditionalWeakTable<DbConnection, EstadoConexion> EstadoPorConexion = new();

    private static readonly TimeSpan EsperaTrasFallo = TimeSpan.FromSeconds(30);

    private readonly string? _cadenaPropietaria;
    private readonly IDataProtectionProvider _proteccion;
    private readonly TimeProvider _reloj;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, ClaveEnMemoria> _clavePorBase = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _falloPorBase = new();
    private readonly SemaphoreSlim _lectura = new(1, 1);

    public TimeSpan Ttl { get; }
    public TimeSpan Rotacion { get; }

    public FirmanteContextoRls(
        IConfiguration configuration, IDataProtectionProvider proteccion, TimeProvider reloj,
        ILogger<FirmanteContextoRls> log)
        : this(configuration.GetConnectionString("CaeManagerDb"), proteccion, reloj,
            TtlPorDefecto, RotacionPorDefecto, log)
    {
    }

    public FirmanteContextoRls(
        string? cadenaPropietaria, IDataProtectionProvider proteccion, TimeProvider reloj,
        TimeSpan ttl, TimeSpan rotacion, ILogger? log = null)
    {
        if (ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        if (rotacion <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(rotacion));
        _cadenaPropietaria = string.IsNullOrWhiteSpace(cadenaPropietaria) ? null : cadenaPropietaria;
        _proteccion = proteccion;
        _reloj = reloj;
        _log = log ?? NullLogger.Instance;
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
        if (token is null)
            return TokenPendiente.SinFirma;

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
        if (token is null)
            return TokenPendiente.SinFirmaOlvidando(conexion);

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
        if (token is null)
            return TokenPendiente.SinFirmaOlvidando(conexion);

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
        /// <summary>
        /// Sin clave no se firma: <c>app.contexto</c> queda vacío y la conexión
        /// no guarda estado en memoria que renovar o sellar.
        /// </summary>
        internal static TokenPendiente SinFirma { get; } = new(string.Empty, () => { });

        /// <summary>
        /// Como <see cref="SinFirma"/>, pero la conexión ya tenía un contexto
        /// firmado: al confirmar se olvida, porque la base deja de tenerlo.
        /// </summary>
        internal static TokenPendiente SinFirmaOlvidando(DbConnection conexion) =>
            new(string.Empty, () => EstadoPorConexion.Remove(conexion));

        public string Token { get; } = token;

        public void Confirmar() => confirmar();
    }

    /// <summary>Olvida el contexto de la conexión (se llama al cerrarla).</summary>
    public static void Olvidar(DbConnection conexion) => EstadoPorConexion.Remove(conexion);

    private async Task<string?> ConstruirAsync(NpgsqlConnection conexion, ContextoSesionRls contexto, CancellationToken ct)
    {
        var clave = await ClaveParaAsync(conexion, ct);
        if (clave is null)
            return null;

        return TokenContextoRls.Construir(
            clave.Leida.Id, clave.Leida.Bytes, contexto, conexion.ProcessID,
            _reloj.GetUtcNow() + Ttl, RandomNumberGenerator.GetHexString(32, lowercase: true));
    }

    private async Task<ClaveEnMemoria?> ClaveParaAsync(NpgsqlConnection conexion, CancellationToken ct)
    {
        var baseDeDatos = $"{conexion.Host}:{conexion.Port}/{conexion.Database}";
        if (_clavePorBase.TryGetValue(baseDeDatos, out var clave) && !DebeReleer(clave))
            return clave;

        await _lectura.WaitAsync(ct);
        try
        {
            if (_clavePorBase.TryGetValue(baseDeDatos, out clave) && !DebeReleer(clave))
                return clave;

            // Tras un fallo no se reintenta en cada comando: el error ya está
            // registrado y /salud lo enseña.
            if (_falloPorBase.TryGetValue(baseDeDatos, out var falloEn)
                && _reloj.GetUtcNow() - falloEn < EsperaTrasFallo)
                return null;

            var leida = await ClaveContextoRls.LeerVigenteAsync(conexion, _proteccion, ct);
            if (leida is null && _cadenaPropietaria is not null)
            {
                var cadena = new NpgsqlConnectionStringBuilder(_cadenaPropietaria);
                if (!string.IsNullOrEmpty(conexion.Database))
                    cadena.Database = conexion.Database;
                await ClaveContextoRls.RegistrarAsync(
                    cadena.ConnectionString, _proteccion, ClaveContextoRls.VigenciaPorDefecto, ct);
                leida = await ClaveContextoRls.LeerVigenteAsync(conexion, _proteccion, ct);
            }

            // Una clave que caduca antes que el token daría 42501 a mitad de su
            // vida: no se usa.
            if (leida is null || leida.ValidaHasta - _reloj.GetUtcNow() <= Ttl)
            {
                _falloPorBase[baseDeDatos] = _reloj.GetUtcNow();
                _clavePorBase.TryRemove(baseDeDatos, out _);
                _log.LogError(
                    "Contexto RLS sin firmar en {BaseDeDatos}: no hay clave vigente que este proceso sepa " +
                    "descifrar, o caduca antes que el token. Relanzar el migrador registra una nueva.",
                    baseDeDatos);
                return null;
            }

            _falloPorBase.TryRemove(baseDeDatos, out _);
            clave = new ClaveEnMemoria(leida, _reloj.GetUtcNow());
            _clavePorBase[baseDeDatos] = clave;
            return clave;
        }
        finally
        {
            _lectura.Release();
        }
    }

    private bool DebeReleer(ClaveEnMemoria clave) =>
        _reloj.GetUtcNow() - clave.LeidaEn >= Rotacion || clave.Leida.ValidaHasta - _reloj.GetUtcNow() <= Ttl;

    private sealed record ClaveEnMemoria(ClaveContextoLeida Leida, DateTimeOffset LeidaEn);

    private sealed record EstadoConexion(
        FirmanteContextoRls Firmante, ContextoSesionRls Base, ContextoSesionRls Vigente, DateTimeOffset FirmadoEn);
}
