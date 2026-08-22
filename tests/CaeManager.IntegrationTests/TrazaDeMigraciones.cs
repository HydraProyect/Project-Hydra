using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Instrumento de diagnóstico, no de producto: registra qué migración está
/// aplicando cada base en cada instante.
///
/// <para>
/// <b>Para qué.</b> El <c>42704: role "cae_app_soporte" does not exist</c> lleva
/// tres apariciones en CI —una, una y cinco fixtures a la vez— siempre dentro de
/// <c>MigrateAsync</c> y siempre en los primeros segundos de la suite. La
/// excepción llega pelada: ni la migración, ni la sentencia, ni el contexto
/// PL/pgSQL. Y no reproduce en local ni con N=2 ni con N=6 sobre la cadena real.
/// Sin saber <b>qué</b> migración estaba aplicándose no se puede clasificar el
/// fallo: no es lo mismo que venga de la que CREA el rol que de una de las dos
/// posteriores que lo PRESUPONEN — son investigaciones distintas.
/// </para>
///
/// <para>
/// <b>Por qué aquí y no en los fixtures.</b> Hay 89 que llaman a
/// <c>MigrateAsync</c>, cada uno con su propio <c>DbContextOptions</c>. Tocarlos
/// uno a uno sería invasivo y, peor, metería trabajo nuevo dentro del camino que
/// queremos observar. <c>DiagnosticListener</c> es el punto que EF ya emite: un
/// único suscriptor del proceso ve los eventos de <b>todos</b> los contextos sin
/// que ninguno cambie de configuración.
/// </para>
///
/// <para>
/// <b>Se activa con <c>CAEMANAGER_TRAZA_MIGRACIONES</c>, cuyo valor es la ruta
/// del fichero de traza.</b> Ausente, no se suscribe a nada.
/// </para>
///
/// <para>
/// <b>No altera semántica ni orden.</b> No toca migraciones, no cambia opciones,
/// no envuelve <c>MigrateAsync</c>. Solo observa. Y queda <b>apagado salvo que se
/// pida</b>: en local la suite corre
/// exactamente igual que antes, y en CI el coste es una línea corta por migración
/// aplicada — del orden de una decena de escrituras por segundo, sobre un
/// fenómeno que ocurre en una ventana de 70 ms. Si midiéramos que aun así
/// desplaza el timing, habríamos introducido otra variable en lo que queremos
/// observar y habría que buscar otra vía.
/// </para>
///
/// <para>
/// <b>Cómo se lee.</b> Dos clases de línea. <c>inicia migracion=…</c> dice qué
/// migración arranca y en qué hilo — EF no expone ahí ni el contexto ni la
/// conexión, así que no dice sobre qué base. <c>ERROR base=… sqlstate=… sql=…</c>
/// sí lleva las tres cosas que hoy faltan: la base, el estado SQL y <b>la
/// sentencia exacta</b> que falló. Con esa línea delante, el <c>42704</c> queda
/// clasificado sin ambigüedad — dirá si el <c>GRANT</c> que revienta es el de la
/// migración que crea el rol o el de una de las dos que lo presuponen.
/// </para>
///
/// <para>
/// Temporal por diseño: se retira cuando la causa raíz esté identificada.
/// </para>
/// </summary>
internal static class TrazaDeMigraciones
{
    private const string Interruptor = "CAEMANAGER_TRAZA_MIGRACIONES";

    private static readonly Lock Candado = new();
    private static StreamWriter? _destino;

    [ModuleInitializer]
    internal static void Activar()
    {
        var ruta = Environment.GetEnvironmentVariable(Interruptor);
        if (string.IsNullOrEmpty(ruta)) return;

        // A fichero y no a consola: `dotnet test` se traga la salida que no
        // pertenece a un test —comprobado—, así que un Console.WriteLine desde
        // el inicializador de módulo o desde un hilo de fondo se pierde entera.
        // Con fichero, CI lo vuelca después del paso de tests.
        _destino = new StreamWriter(ruta, append: true) { AutoFlush = true };
        Escribir($"traza activada · proceso {Environment.ProcessId}");

        DiagnosticListener.AllListeners.Subscribe(new BuscadorDeListeners());
    }

    private static void Escribir(string mensaje)
    {
        if (_destino is null) return;
        lock (Candado) _destino.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {mensaje}");
    }

    /// <summary>
    /// Un fallo del instrumento no puede tumbar la suite: lo que se está
    /// diagnosticando es lo bastante escurridizo como para que un test rojo de
    /// más contamine la lectura.
    /// </summary>
    private static void Anotar(KeyValuePair<string, object?> evento)
    {
        try
        {
            switch (evento.Value)
            {
                // Qué migración empieza. MigrationEventData no expone ni el
                // contexto ni la conexión, así que esto por sí solo no dice
                // sobre QUÉ base ocurre — lo aporta el evento de error.
                // Abre cadena: es el único evento de migración que sí trae la
                // conexión, y por tanto lo que permite atribuir a una base las
                // líneas de migración que vienen detrás en ese mismo hilo.
                case MigratorConnectionEventData cadena:
                    Escribir($"abre cadena base={cadena.Connection.Database} " +
                             $"hilo={Environment.CurrentManagedThreadId}");
                    break;

                case MigrationEventData migracion:
                    Escribir($"inicia migracion={migracion.Migration.GetType().Name} " +
                             $"hilo={Environment.CurrentManagedThreadId}");
                    break;

                // El dato decisivo: el comando que falló, con su SQL y su base.
                // Es lo que hoy no tenemos y sin lo cual no se puede clasificar
                // el 42704.
                case CommandErrorEventData error:
                    Escribir($"ERROR base={error.Command.Connection?.Database ?? "?"} " +
                             $"sqlstate={(error.Exception as PostgresException)?.SqlState ?? "?"} " +
                             $"hilo={Environment.CurrentManagedThreadId} " +
                             $"mensaje={Una(error.Exception.Message)} " +
                             $"sql={Una(error.Command.CommandText)}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Escribir($"la propia traza fallo: {ex.GetType().Name}");
        }
    }

    /// <summary>En una línea y acotado: el SQL de una migración puede ser enorme.</summary>
    private static string Una(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return "(vacio)";
        var plano = texto.ReplaceLineEndings(" ").Trim();
        return plano.Length <= 400 ? plano : plano[..400] + "...";
    }

    private sealed class BuscadorDeListeners : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore") listener.Subscribe(new ObservadorDeMigraciones());
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private sealed class ObservadorDeMigraciones : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> evento)
        {
            // Solo dos eventos. El inicio de cada migración da el contexto; el
            // error de comando da el dato decisivo y es de volumen mínimo por
            // definición. Suscribirse a CommandExecuting multiplicaría el
            // volumen por cien y añadiría trabajo real al camino que se quiere
            // dejar intacto.
            if (evento.Key == RelationalEventId.MigrationApplying.Name
                || evento.Key == RelationalEventId.MigrateUsingConnection.Name
                || evento.Key == RelationalEventId.CommandError.Name) Anotar(evento);
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }
}
