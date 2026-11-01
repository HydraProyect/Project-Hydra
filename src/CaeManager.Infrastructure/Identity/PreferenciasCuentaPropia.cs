using CaeManager.Application.Common;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CaeManager.Infrastructure.Identity;

/// <summary>Las preferencias de una cuenta y su última actividad, sin credenciales (ASVS 4.0.3 V4.1.3).</summary>
/// <param name="Tema">El tema de la interfaz.</param>
/// <param name="Idioma">El idioma de la interfaz.</param>
/// <param name="UltimaActividadUtc">La última interacción autenticada registrada, si la hay.</param>
public sealed record PreferenciasDeCuenta(TemaPreferido Tema, IdiomaPreferido Idioma, DateTime? UltimaActividadUtc);

/// <summary>Cómo terminó una escritura de <see cref="IPreferenciasCuentaPropia"/>.</summary>
public enum ResultadoEscrituraCuenta
{
    /// <summary>La fila quedó escrita: el llamador ya puede dar el valor por persistido.</summary>
    Guardada,

    /// <summary>Esta conexión no ve la cuenta, o ya no existe.</summary>
    CuentaNoVisible,

    /// <summary>Otra escritura ganó la carrera en los dos intentos: la fila sigue como la dejó ella.</summary>
    Conflicto,
}

/// <summary>
/// Lee y escribe lo que el circuito o la petición guardan de su propia cuenta sin cargarla entera: en una Sesión
/// Privilegiada la conexión lleva <c>cae_app_soporte</c>, y una cuenta completa arrastraría <c>PasswordHash</c> y
/// <c>SecurityStamp</c>. No autoriza nada: quien llama pasa el identificador de la cuenta de la sesión, nunca uno que
/// venga de la petición. Solo se admiten los consumidores congelados en <c>MaterializacionesDeCuentasEnWebTests</c>.
/// </summary>
public interface IPreferenciasCuentaPropia
{
    /// <summary>Las preferencias de la cuenta, o <c>null</c> si esta conexión no la ve.</summary>
    Task<PreferenciasDeCuenta?> ObtenerAsync(Guid usuarioId, CancellationToken ct = default);

    /// <summary>Guarda el tema; con un conflicto reintenta una vez sobre la fila vigente.</summary>
    Task<ResultadoEscrituraCuenta> GuardarTemaAsync(Guid usuarioId, TemaPreferido tema, CancellationToken ct = default);

    /// <summary>Guarda el idioma; con un conflicto reintenta una vez sobre la fila vigente.</summary>
    Task<ResultadoEscrituraCuenta> GuardarIdiomaAsync(Guid usuarioId, IdiomaPreferido idioma, CancellationToken ct = default);

    /// <summary>
    /// Registra la actividad; con un conflicto reintenta una vez sobre la fila vigente, sin retroceder una actividad
    /// posterior que ya esté guardada.
    /// </summary>
    Task<ResultadoEscrituraCuenta> RegistrarActividadAsync(Guid usuarioId, DateTime instanteUtc, CancellationToken ct = default);
}

/// <summary>
/// Implementación de <see cref="IPreferenciasCuentaPropia"/>. Pasa por <see cref="PuertaAccesoDatos"/> como
/// <see cref="AvatarDeCuentasIdentity"/>: el layout la usa en paralelo con el resto de componentes sobre el mismo
/// <c>CaeManagerDbContext</c>.
///
/// <para>
/// <b>Control de concurrencia.</b> Cada intento lee en proyección el <c>ConcurrencyStamp</c> vigente y solo escribe si
/// la fila lo conserva; la misma escritura lo renueva, así que quien tenga la cuenta cargada de antes y la guarde
/// entera con <c>UserManager.UpdateAsync</c> falla por concurrencia en vez de devolver el valor anterior. Con otra
/// escritura en medio se relee y se reintenta una sola vez: no hay nada que fusionar, solo se toca esta columna.
/// </para>
///
/// <para>
/// <b>Por <c>SaveChanges</c> y no por <c>ExecuteUpdate</c>.</b> Se adjunta un esqueleto con la clave y los valores
/// leídos, y solo salen en el <c>UPDATE</c> la columna cambiada y el sello. Así la escritura conserva lo que daba
/// <c>UserManager.UpdateAsync</c> y <c>ExecuteUpdate</c> se saltaría: la fila de auditoría de <c>Tema</c> e
/// <c>Idioma</c> (<c>AuditoriaInterceptor</c>; la de <c>UltimaActividadUtc</c> es silenciosa), su sellado con el Tenant
/// de la cuenta (por eso el esqueleto lleva <c>TenantId</c>) y el <c>DbUpdateException</c> con el 42501 de
/// <c>cae_app_soporte</c>, que no tiene <c>UPDATE</c> sobre la tabla y al que este puerto no le concede ninguno.
/// </para>
///
/// <para>
/// <b>El esqueleto no sobrevive a la escritura.</b> Está casi vacío, y una lectura posterior del circuito lo recibiría
/// del mapa de identidad como si fuera la cuenta. Una cuenta ya rastreada por otro camino se trata aparte: si conserva
/// el sello vigente se escribe sobre ella, de modo que su dueño ve el valor nuevo y puede seguir guardando; si tiene
/// otro sello está obsoleta, sus cambios ya no podrían guardarse, y se desengancha igual que hacen
/// <see cref="GestionCuentasUsuarioIdentity"/> y <see cref="SegundoFactorDeCuentasIdentity"/>, para que la siguiente
/// lectura venga de la base. Una escritura fallida también la saca del rastreo, como a todo lote fallido: esa instancia
/// conserva en memoria valores que no llegaron a la fila y no debe reutilizarse.
/// </para>
///
/// <para>
/// <b>La cancelación llega hasta la escritura, no a ella.</b> Se puede abandonar la espera en la puerta o la lectura;
/// el <c>SaveChanges</c> empezado termina, como con <c>UserManager.UpdateAsync</c>, que no recibe el token. Navegar
/// fuera de la pantalla no debe convertirse en un rechazo que saque la cuenta del rastreo.
/// </para>
/// </summary>
public sealed class PreferenciasCuentaPropia(CaeManagerDbContext contexto, PuertaAccesoDatos puertaAccesoDatos)
    : IPreferenciasCuentaPropia
{
    private const int Intentos = 2;

    public Task<PreferenciasDeCuenta?> ObtenerAsync(Guid usuarioId, CancellationToken ct = default) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            var fila = await LeerAsync(usuarioId, ct);
            return fila is null ? null : new PreferenciasDeCuenta(fila.Tema, fila.Idioma, fila.UltimaActividadUtc);
        }, ct);

    public Task<ResultadoEscrituraCuenta> GuardarTemaAsync(Guid usuarioId, TemaPreferido tema, CancellationToken ct = default) =>
        EscribirAsync(usuarioId, cuenta => cuenta.Tema = tema, ct);

    public Task<ResultadoEscrituraCuenta> GuardarIdiomaAsync(Guid usuarioId, IdiomaPreferido idioma, CancellationToken ct = default) =>
        EscribirAsync(usuarioId, cuenta => cuenta.Idioma = idioma, ct);

    public Task<ResultadoEscrituraCuenta> RegistrarActividadAsync(Guid usuarioId, DateTime instanteUtc, CancellationToken ct = default) =>
        EscribirAsync(usuarioId, cuenta =>
        {
            if (cuenta.UltimaActividadUtc is null || cuenta.UltimaActividadUtc < instanteUtc)
                cuenta.UltimaActividadUtc = instanteUtc;
        }, ct);

    private Task<ResultadoEscrituraCuenta> EscribirAsync(Guid usuarioId, Action<ApplicationUser> aplicar, CancellationToken ct) =>
        puertaAccesoDatos.EjecutarAsync(async () =>
        {
            for (var intento = 0; intento < Intentos; intento++)
            {
                var fila = await LeerAsync(usuarioId, ct);
                if (fila is null)
                    return ResultadoEscrituraCuenta.CuentaNoVisible;

                if (await IntentarAsync(usuarioId, fila, aplicar))
                    return ResultadoEscrituraCuenta.Guardada;
            }

            return ResultadoEscrituraCuenta.Conflicto;
        }, ct);

    private async Task<bool> IntentarAsync(Guid usuarioId, FilaDeCuenta fila, Action<ApplicationUser> aplicar)
    {
        var rastreada = RastreadaVigente(usuarioId, fila.Sello);
        var entrada = rastreada ?? contexto.Attach(Esqueleto(usuarioId, fila));

        aplicar(entrada.Entity);
        entrada.Entity.ConcurrencyStamp = Guid.NewGuid().ToString();

        try
        {
            await contexto.SaveChangesAsync(CancellationToken.None);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // CaeManagerDbContext ya descartó el lote y su auditoría antes de relanzar.
            return false;
        }
        catch (Exception)
        {
            // Lo mismo para cualquier otro rechazo: un guardado posterior del circuito no debe arrastrarlo.
            ConcurrenciaContexto.DescartarEdicionPerdida(contexto, [entrada]);
            throw;
        }
        finally
        {
            if (rastreada is null)
                entrada.State = EntityState.Detached;
        }
    }

    /// <summary>La cuenta rastreada si conserva el sello de la fila; si está obsoleta, sale del rastreo.</summary>
    private EntityEntry<ApplicationUser>? RastreadaVigente(Guid usuarioId, string? sello)
    {
        var entrada = contexto.ChangeTracker.Entries<ApplicationUser>().FirstOrDefault(e => e.Entity.Id == usuarioId);
        if (entrada is null)
            return null;

        if (entrada.Property(u => u.ConcurrencyStamp).OriginalValue == sello)
            return entrada;

        entrada.State = EntityState.Detached;
        return null;
    }

    private static ApplicationUser Esqueleto(Guid usuarioId, FilaDeCuenta fila) => new()
    {
        Id = usuarioId,
        TenantId = fila.TenantId,
        ConcurrencyStamp = fila.Sello,
        Tema = fila.Tema,
        Idioma = fila.Idioma,
        UltimaActividadUtc = fila.UltimaActividadUtc,
    };

    private Task<FilaDeCuenta?> LeerAsync(Guid usuarioId, CancellationToken ct) =>
        contexto.Users.AsNoTracking()
            .Where(u => u.Id == usuarioId)
            .Select(u => new FilaDeCuenta(u.TenantId, u.ConcurrencyStamp, u.Tema, u.Idioma, u.UltimaActividadUtc))
            .FirstOrDefaultAsync(ct);

    private sealed record FilaDeCuenta(
        Guid TenantId, string? Sello, TemaPreferido Tema, IdiomaPreferido Idioma, DateTime? UltimaActividadUtc);
}
