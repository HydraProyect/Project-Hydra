using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;

namespace CaeManager.Web.Services;

/// <summary>
/// Pendientes por empresa gestionada para el selector de Tenant beneficiario. No calcula nada propio:
/// reutiliza el recuento por Tenant que ya produce <see cref="ObtenerMiTrabajoAgregadoQuery"/> (el de
/// Mi trabajo, <c>Resumen.TotalAcciones</c>), con su misma autorización y su RLS sellado Tenant a Tenant
/// dentro de la Application. El componente nunca consulta Tenants.
/// </summary>
public interface IContadorPendientesSelectorTenant
{
    /// <summary>
    /// Pendientes por Tenant, o <c>null</c> si el cálculo falló o no terminó a tiempo (el selector se
    /// pinta sin contador). Solo contiene Tenants de <paramref name="autorizados"/> que no son el de origen.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>?> ObtenerAsync(
        Guid usuarioId, Guid tenantActivoId, IReadOnlyList<ClienteAutorizadoDto> autorizados,
        Func<CancellationToken, Task<MiTrabajoAgregadoDto>> calcular, CancellationToken cancellationToken);

    /// <summary>Descarta lo calculado para ese usuario (cambio de empresa).</summary>
    void Invalidar(Guid usuarioId);
}

/// <summary>
/// Caché de vida corta del cálculo anterior. La clave lleva el usuario, el Tenant activo, el alcance
/// (huella del conjunto autorizado que el propio componente acaba de leer, con su rol de cartera) y la
/// vigencia (cubo temporal de <see cref="Vida"/>): un cambio de cualquiera de ellos es otra entrada, y
/// nada calculado para un usuario o un alcance se sirve a otro. Al leer, el resultado se vuelve a
/// recortar al conjunto autorizado actual, así que un Tenant fuera del alcance ni aparece ni cuenta.
/// Los fallos no se cachean. Los cálculos simultáneos de una misma clave se comparten.
/// </summary>
public sealed class ContadorPendientesSelectorTenant(TimeProvider reloj) : IContadorPendientesSelectorTenant
{
    public static readonly TimeSpan Vida = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TopeDelCalculo = TimeSpan.FromSeconds(10);
    private const int TopeDeEntradas = 256;

    private sealed record Resultado(IReadOnlyDictionary<Guid, int> Pendientes, bool Completo);

    private sealed record Entrada(Guid UsuarioId, DateTimeOffset Caduca, Lazy<Task<Resultado>> Calculo);

    private readonly ConcurrentDictionary<string, Entrada> _entradas = new();

    public async Task<IReadOnlyDictionary<Guid, int>?> ObtenerAsync(
        Guid usuarioId, Guid tenantActivoId, IReadOnlyList<ClienteAutorizadoDto> autorizados,
        Func<CancellationToken, Task<MiTrabajoAgregadoDto>> calcular, CancellationToken cancellationToken)
    {
        var ahora = reloj.GetUtcNow();
        Podar(ahora);

        var clave = Clave(usuarioId, tenantActivoId, autorizados, ahora);
        var entrada = _entradas.GetOrAdd(clave, _ => new Entrada(
            usuarioId, ahora + Vida,
            new Lazy<Task<Resultado>>(() => CalcularAsync(calcular), LazyThreadSafetyMode.ExecutionAndPublication)));

        try
        {
            var calculado = await entrada.Calculo.Value.WaitAsync(cancellationToken);
            // Un resultado parcial (un Tenant cuya cola no se pudo construir) sirve a quien lo pidió, pero no
            // se guarda: un Tenant no consultado no es un Tenant al día.
            if (!calculado.Completo) Descartar(clave, entrada);
            var permitidos = autorizados.Where(a => !a.EsOrigen).Select(a => a.TenantId).ToHashSet();
            return calculado.Pendientes.Where(p => permitidos.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Quien espera dejó de esperar; el cálculo compartido sigue y puede servir a otros.
            return null;
        }
        catch (Exception)
        {
            Descartar(clave, entrada);
            return null;
        }
    }

    /// <summary>Quita esa entrada y no otra con la misma clave que ya la sustituyó.</summary>
    private void Descartar(string clave, Entrada entrada) =>
        ((ICollection<KeyValuePair<string, Entrada>>)_entradas).Remove(new KeyValuePair<string, Entrada>(clave, entrada));

    public void Invalidar(Guid usuarioId)
    {
        foreach (var par in _entradas.Where(p => p.Value.UsuarioId == usuarioId).ToList())
            _entradas.TryRemove(par.Key, out _);
    }

    private static async Task<Resultado> CalcularAsync(Func<CancellationToken, Task<MiTrabajoAgregadoDto>> calcular)
    {
        // El tope cancela de verdad las consultas: el cálculo ocupa la puerta de acceso a datos del circuito
        // que lo pidió, y no puede retenerla más de lo que el selector está dispuesto a esperar.
        using var tope = new CancellationTokenSource(TopeDelCalculo);
        var datos = await calcular(tope.Token);
        // Misma regla que la cartera de Mi trabajo: el Tenant de origen no es una empresa gestionada.
        return new Resultado(
            datos.Tenants.Where(t => !t.EsOrigen).ToDictionary(t => t.TenantId, t => t.Resumen.TotalAcciones),
            Completo: datos.NoConsultados.Count == 0);
    }

    private void Podar(DateTimeOffset ahora)
    {
        foreach (var par in _entradas.Where(p => p.Value.Caduca <= ahora).ToList())
            _entradas.TryRemove(par.Key, out _);

        if (_entradas.Count > TopeDeEntradas)
            _entradas.Clear();
    }

    internal static string Clave(Guid usuarioId, Guid tenantActivoId, IReadOnlyList<ClienteAutorizadoDto> autorizados, DateTimeOffset ahora)
    {
        var alcance = string.Join(';', autorizados
            .OrderBy(a => a.TenantId)
            .Select(a => $"{a.TenantId:N}:{(a.EsOrigen ? 'o' : '-')}{(a.EsGestionadoPorOperacion ? 'g' : '-')}{(a.EsCarteraGestorCae ? 'c' : '-')}"));
        var huella = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(alcance)));
        var vigencia = ahora.UtcTicks / Vida.Ticks;
        return $"{usuarioId:N}|{tenantActivoId:N}|{huella}|{vigencia}";
    }
}
