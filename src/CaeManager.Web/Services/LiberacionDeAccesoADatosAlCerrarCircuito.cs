using CaeManager.Application.Common;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace CaeManager.Web.Services;

/// <summary>
/// Al cerrarse el circuito, cierra la <see cref="PuertaAccesoDatos"/> y espera a
/// que la operación de datos que estuviera en vuelo termine, <b>antes</b> de
/// que el framework dispone el scope de DI y con él el <c>CaeManagerDbContext</c>
/// y su conexión.
///
/// <para>
/// El framework dispone el scope del circuito sin esperar a los componentes que
/// siguen inicializándose (<c>OnInitializedAsync</c> de layout y página no están
/// bajo su control), así que un cierre a mitad de una consulta dispone el
/// contexto mientras otro hilo lee de su conexión. Medido con PostgreSQL real:
/// <c>ArgumentOutOfRangeException</c> en <c>NpgsqlDataReader.ProcessMessage</c>,
/// <c>ObjectDisposedException</c>, cuelgues, y conexiones que vuelven al pool
/// con el protocolo desincronizado y rompen la primera consulta de otro
/// circuito (en el E2E de CI, la navegación siguiente del mismo test). Ver
/// <see cref="PuertaAccesoDatos"/>.
/// </para>
///
/// <para>
/// El orden lo fija el framework, no este fichero: <c>CircuitHost.DisposeAsync</c>
/// llama a <see cref="CircuitHandler.OnCircuitClosedAsync"/> de todos los
/// handlers y solo después dispone el renderer y el scope. La espera es un
/// <c>await</c>, no un bloqueo: la operación en vuelo necesita el despachador
/// del circuito para reanudar sus continuaciones, y un <c>await</c> se lo deja
/// libre.
/// </para>
///
/// <para>
/// <b>Scoped</b>, como <see cref="EstadoDelCircuito"/>: en Blazor Server el
/// scope de DI es el del circuito, así que la <see cref="PuertaAccesoDatos"/>
/// que recibe es exactamente la que usan los componentes de ese circuito.
/// </para>
/// </summary>
public sealed class LiberacionDeAccesoADatosAlCerrarCircuito(PuertaAccesoDatos puerta) : CircuitHandler
{
    /// <summary>
    /// Tope de la espera a la operación en vuelo. Holgado frente a una consulta de
    /// pantalla normal (el <c>CommandTimeout</c> de EF es 30 s: una consulta
    /// colgada agota el tope en vez de retener el cierre para siempre).
    /// </summary>
    internal static readonly TimeSpan EsperaMaxima = TimeSpan.FromSeconds(10);

    public override async Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
        => await puerta.CerrarAsync(EsperaMaxima);
}
