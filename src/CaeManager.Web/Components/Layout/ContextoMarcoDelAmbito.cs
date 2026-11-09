using CaeManager.Application.Common;
using CaeManager.Application.Marco.Queries.ObtenerContextoMarco;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// El contexto del marco (<see cref="ObtenerContextoMarcoQuery"/>) de UN ámbito de DI —la petición HTTP del
/// prerenderizado o el circuito—, compartido por todos los componentes del marco: NavMenu, MenuUsuario,
/// SelectorClienteActivo y MigasPagina lo piden aquí en vez de despachar cada uno sus Queries.
///
/// <para>
/// <b>Un despacho por ámbito.</b> La primera petición lanza la Query y las demás esperan la misma tarea, también
/// las simultáneas (los islotes del circuito se inicializan a la vez). Caduca a los <see cref="MemoPorContexto.Caducidad"/>
/// segundos contados desde que empezó su lectura, y se descarta antes y después de cada Command porque se registra
/// también como <see cref="IMemoPorContexto"/> (<c>InvalidacionAlcanceBehavior</c>). Las Queries que compone tienen sus
/// propios memos del ámbito, así que lo que sirve puede tener hasta dos caducidades de antigüedad, igual que el estado
/// persistido que tenían antes el selector y las migas; lo acota además la caducidad del alcance de la cartera, y nada
/// de esto autoriza. Una tarea que falla no se queda, ni un contexto degradado (alguna parte de presentación se
/// toleró con su valor sin oferta, <see cref="ContextoMarcoDto.Degradado"/>): se entrega a quien lo esperaba, no se
/// guarda ni se persiste, y la siguiente petición vuelve a leer.
/// </para>
///
/// <para>
/// <b>Del prerenderizado al circuito.</b> Lo leído en el prerenderizado se persiste con
/// <see cref="PersistentComponentState"/> (cifrado con Data Protection, como el resto del estado persistido) y el
/// circuito lo toma en vez de volver a leerlo, con el mismo criterio que tenían el selector y las migas: el mismo
/// contexto exacto (<see cref="ClaveContextoMemo"/> más el Tenant beneficiario y la Asignación de Operación
/// seleccionados), una edad dentro de la caducidad, una cartera pequeña y un tamaño serializado acotado (el estado
/// vuelve en el mensaje de arranque del circuito, 32 KB por defecto, junto al del resto de componentes). Un Command
/// en el ámbito antes de la primera lectura también descarta lo persistido: pudo leerse antes de la escritura.
/// </para>
///
/// <para>
/// <b>Sin reutilización.</b> Sin usuario o sin Tenant de origen (no hay clave), con la lente de demo o la vista previa
/// de vocabulario activas (no entran en la clave: la lista es un recorte de la real) y con una Sesión Privilegiada
/// seleccionada (el Actor de Plataforma TALVEG no comparte nada con otro ámbito), cada petición despacha la Query
/// y nada se persiste. Las lecturas que fallan cerradas no pasan por aquí (ver <see cref="ObtenerContextoMarcoQuery"/>).
/// </para>
///
/// Lo que devuelve no autoriza nada: decide qué se ofrece; los endpoints, los Commands y RLS deciden con el dato
/// vigente (OWASP ASVS 4.0.3 V4.1.1).
/// </summary>
public sealed class ContextoMarcoDelAmbito : IMemoPorContexto, IDisposable
{
    /// <summary>Clave del estado persistido (pública para los tests: sin InternalsVisibleTo en CaeManager.Web).</summary>
    public const string ClavePersistencia = "layout-contexto-marco-v1";

    /// <summary>Tope de Tenants beneficiarios que se persisten para el circuito; con más, el circuito vuelve a leer.</summary>
    public const int MaximoEmpresasPersistidas = 25;

    /// <summary>
    /// Tope del estado serializado, antes de cifrarlo (Data Protection y base64 lo inflan): con el orden del menú, que
    /// admite listas largas, la cartera sola no lo acota. Por encima, el circuito vuelve a leer.
    /// </summary>
    public const int MaximoBytesPersistidos = 8 * 1024;

    /// <summary>
    /// Todo lo que tiene que coincidir para reutilizar lo leído: la clave de los memos y la selección entera (Tenant
    /// beneficiario y Asignación de Operación por la que se abrió: el mismo usuario puede tener varias sobre un Tenant).
    /// </summary>
    public sealed record ContextoLectura(ClaveContextoMemo Clave, Guid? Seleccionado, Guid? AsignacionSeleccionada = null);

    /// <summary>Lo que el prerenderizado deja al circuito.</summary>
    public sealed record ContextoPersistido(ContextoLectura Contexto, DateTimeOffset Creado, ContextoMarcoDto Marco);

    private sealed record Entrada(ContextoLectura Contexto, DateTimeOffset Creado, Task<ContextoMarcoDto> Tarea);

    private readonly IMediator _mediador;
    private readonly IServiceProvider _servicios;
    private readonly TimeProvider _reloj;
    private readonly PersistentComponentState? _estadoPersistente;
    private PersistingComponentStateSubscription? _suscripcion;
    private readonly object _cerrojo = new();
    private Entrada? _entrada;
    private bool _restauracionIntentada;

    public ContextoMarcoDelAmbito(IMediator mediador, IServiceProvider servicios)
    {
        _mediador = mediador;
        _servicios = servicios;
        _reloj = servicios.GetService<TimeProvider>() ?? TimeProvider.System;
        // Opcional: un host sin componentes interactivos (o un test) no lo registra, y entonces solo se memoiza en el ámbito.
        _estadoPersistente = servicios.GetService<PersistentComponentState>();
    }

    /// <summary>El contexto del marco del ámbito actual.</summary>
    public async Task<ContextoMarcoDto> ObtenerAsync(CancellationToken cancellationToken = default)
    {
        var contexto = await ResolverContextoAsync();
        if (contexto is null)
            return await _mediador.Send(new ObtenerContextoMarcoQuery(), cancellationToken);

        Task<ContextoMarcoDto> tarea;
        TaskCompletionSource<ContextoMarcoDto>? propia = null;
        lock (_cerrojo)
        {
            var ahora = _reloj.GetUtcNow();
            if (_entrada is { } entrada && entrada.Contexto == contexto && Vigente(entrada.Creado, ahora))
                tarea = entrada.Tarea;
            else if (Restaurar(contexto, ahora) is { } restaurada)
            {
                _entrada = restaurada;
                tarea = restaurada.Tarea;
            }
            else
            {
                // La entrada se guarda antes de despachar, para que las peticiones simultáneas esperen esta lectura; el
                // despacho va fuera del cerrojo, que no debe cubrir el pipeline de MediatR.
                propia = new TaskCompletionSource<ContextoMarcoDto>(TaskCreationOptions.RunContinuationsAsynchronously);
                tarea = propia.Task;
                _entrada = new Entrada(contexto, ahora, tarea);
                SuscribirPersistencia();
            }
        }
        if (propia is not null)
            _ = LeerAsync(propia);

        try
        {
            return await tarea.WaitAsync(cancellationToken);
        }
        catch when (tarea.IsFaulted || tarea.IsCanceled)
        {
            lock (_cerrojo)
            {
                if (ReferenceEquals(_entrada?.Tarea, tarea)) _entrada = null;
            }
            throw;
        }
    }

    /// <summary>Lo descarta todo. Lo llama <c>InvalidacionAlcanceBehavior</c> antes y después de cada Command.</summary>
    public void Invalidar()
    {
        // Una lectura en vuelo se entrega a quien ya la esperaba, pero no se guarda: pudo leerse antes de la escritura.
        // Por lo mismo, lo que dejó el prerenderizado y aún no se tomó ya no se toma.
        lock (_cerrojo)
        {
            _entrada = null;
            _restauracionIntentada = true;
        }
    }

    public void Dispose() => _suscripcion?.Dispose();

    /// <summary>
    /// El despacho compartido, en el flujo de quien lo empieza. Sin el token de quien llama: la lectura la esperan
    /// varios componentes y no puede cancelarla uno solo; quien se cansa de esperar se va con su propio token.
    /// </summary>
    private async Task LeerAsync(TaskCompletionSource<ContextoMarcoDto> propia)
    {
        try
        {
            var marco = await _mediador.Send(new ObtenerContextoMarcoQuery(), CancellationToken.None);
            // Degradado: se entrega a quien ya espera, pero se suelta antes de completarse, para que nadie más lo tome
            // del ámbito ni lo persista al circuito (la persistencia solo mira la entrada guardada).
            if (marco.Degradado)
                lock (_cerrojo)
                {
                    if (ReferenceEquals(_entrada?.Tarea, propia.Task)) _entrada = null;
                }
            propia.TrySetResult(marco);
        }
        catch (OperationCanceledException cancelacion)
        {
            propia.TrySetCanceled(cancelacion.CancellationToken);
        }
        catch (Exception excepcion)
        {
            propia.TrySetException(excepcion);
        }
    }

    private bool Vigente(DateTimeOffset creado, DateTimeOffset ahora) =>
        ahora - creado is var edad && edad >= TimeSpan.Zero && edad < MemoPorContexto.Caducidad;

    /// <summary>
    /// Lo que dejó el prerenderizado, una sola vez por ámbito (<c>TryTakeFromJson</c> lo consume), si es del mismo
    /// contexto exacto y sigue dentro de la caducidad. Un estado ilegible se ignora y se lee.
    /// </summary>
    private Entrada? Restaurar(ContextoLectura contexto, DateTimeOffset ahora)
    {
        if (_restauracionIntentada || _estadoPersistente is null) return null;
        _restauracionIntentada = true;
        try
        {
            return _estadoPersistente.TryTakeFromJson<ContextoPersistido>(ClavePersistencia, out var anterior)
                && anterior is not null && anterior.Contexto == contexto && Vigente(anterior.Creado, ahora)
                && anterior.Marco.ClientesAutorizados is not null && !anterior.Marco.Degradado
                ? new Entrada(contexto, anterior.Creado, Task.FromResult(anterior.Marco))
                : null;
        }
        catch (Exception)
        {
            // Ilegible o incompleto (p. ej. sin «Marco»): se lee, como en AceptacionTerminosGate.
            return null;
        }
    }

    /// <summary>
    /// Se suscribe al guardado del estado la primera vez que este ámbito lee, y no al construirse: el servicio también
    /// se crea en ámbitos sin componentes (cualquier Command lo resuelve como <see cref="IMemoPorContexto"/>). Con modo
    /// explícito porque quien registra no es un componente y el estado solo sirve a los islotes del circuito. Si el
    /// guardado ya está en curso no se admite registrar: se queda sin persistir y el circuito vuelve a leer.
    /// </summary>
    private void SuscribirPersistencia()
    {
        if (_suscripcion is not null || _estadoPersistente is null) return;
        try
        {
            _suscripcion = _estadoPersistente.RegisterOnPersisting(PersistirAsync, RenderMode.InteractiveServer);
        }
        catch (InvalidOperationException)
        {
            // Sin persistencia: solo cuesta una lectura más en el circuito.
        }
    }

    private Task PersistirAsync()
    {
        Entrada? entrada;
        lock (_cerrojo) entrada = _entrada;

        // Solo lo leído en este ámbito y ya terminado bien. Lo restaurado no se reenvía sin necesidad de marcarlo: la
        // suscripción solo nace con una lectura propia, que ocurre después del único intento de restaurar y sustituye
        // a la entrada restaurada, así que con suscripción la entrada guardada nunca es una restaurada. Un contexto
        // degradado se suelta antes de completarse (LeerAsync), pero esta comprobación va fuera del cerrojo y la
        // entrada pudo tomarse aún en vuelo: por eso se mira también aquí.
        if (entrada is not { Tarea.IsCompletedSuccessfully: true } leida
            || leida.Tarea.Result.Degradado
            || leida.Tarea.Result.ClientesAutorizados.Count > MaximoEmpresasPersistidas)
            return Task.CompletedTask;

        var persistido = new ContextoPersistido(leida.Contexto, leida.Creado, leida.Tarea.Result);
        if (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(persistido).Length <= MaximoBytesPersistidos)
            _estadoPersistente!.PersistAsJson(ClavePersistencia, persistido);
        return Task.CompletedTask;
    }

    /// <summary>
    /// La clave de lectura, o <c>null</c> cuando no se reutiliza nada (ver el comentario de la clase). Todo sale de
    /// claims, cookies y el <c>AsyncLocal</c> del ámbito explícito, salvo la lente de demo, que la cachea su servicio.
    /// </summary>
    private async Task<ContextoLectura?> ResolverContextoAsync()
    {
        var seleccion = _servicios.GetService<IClienteActivoSeleccionado>();
        // Sesión Privilegiada por cualquiera de sus dos coordenadas: el Actor de Plataforma TALVEG no comparte lectura.
        if (_servicios.GetService<CaeManager.Application.Plataforma.ICoordenadaSesionPrivilegiada>() is { HaySeleccionada: true }
            || seleccion?.SesionPrivilegiadaIdSeleccionada is not null)
            return null;
        if (_servicios.GetService<CaeManager.Application.Tenants.IVistaVocabularioPreviewService>()?.PerfilForzado is not null)
            return null;
        if (_servicios.GetService<CaeManager.Application.VistaDemo.IVistaDemoActual>() is { } vistaDemo
            && await vistaDemo.ObtenerEfectivaAsync() is not null)
            return null;
        if (_servicios.GetService<ICurrentUserService>() is not { } usuario
            || _servicios.GetService<ITenantActual>() is not { } tenant
            || await ClaveContextoMemo.ResolverAsync(usuario, tenant) is not { } clave)
            return null;

        return new ContextoLectura(clave, seleccion?.TenantIdSeleccionado, seleccion?.AsignacionOperacionIdSeleccionada);
    }
}
