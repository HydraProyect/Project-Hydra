using System.Runtime.InteropServices;

namespace CaeManager.Application.Tests;

/// <summary>
/// Tiempo de CPU consumido por el hilo que llama: solo cuenta mientras ese hilo
/// ejecuta, no mientras espera turno. Sirve para acotar el coste de un algoritmo en
/// un test sin que la contención de la máquina (otras compilaciones, otros tests en
/// paralelo, prioridad baja) lo ponga en rojo: un <see cref="System.Diagnostics.Stopwatch"/>
/// mide reloj de pared y crece igual cuando el hilo está parado.
/// <para>
/// Windows: <c>GetThreadTimes</c> (núcleo + usuario). Linux: <c>clock_gettime</c> con
/// <c>CLOCK_THREAD_CPUTIME_ID</c>. En cualquier otro sistema lanza: un instrumento
/// que no puede medir no debe dar un verde.
/// </para>
/// <para>
/// El código medido tiene que ser síncrono: un <c>await</c> puede reanudar en otro
/// hilo y la resta ya no correspondería al trabajo medido.
/// </para>
/// </summary>
internal static class TiempoCpuDelHilo
{
    public static TimeSpan Actual()
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var nucleo, out var usuario))
                throw new InvalidOperationException($"GetThreadTimes falló: {Marshal.GetLastPInvokeError()}");
            return TimeSpan.FromTicks(nucleo + usuario); // FILETIME: unidades de 100 ns, igual que un tick.
        }

        if (OperatingSystem.IsLinux())
        {
            if (clock_gettime(ClockThreadCputimeIdLinux, out var t) != 0)
                throw new InvalidOperationException($"clock_gettime falló: {Marshal.GetLastPInvokeError()}");
            return TimeSpan.FromTicks(t.Segundos * TimeSpan.TicksPerSecond + t.Nanosegundos / 100);
        }

        throw new PlatformNotSupportedException("TiempoCpuDelHilo solo sabe medir en Windows y Linux.");
    }

    /// <summary>Ejecuta <paramref name="accion"/> y devuelve su resultado y el tiempo de CPU que gastó este hilo.</summary>
    public static (T Resultado, TimeSpan Cpu) Medir<T>(Func<T> accion)
    {
        var inicio = Actual();
        var resultado = accion();
        return (resultado, Actual() - inicio);
    }

    private const int ClockThreadCputimeIdLinux = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Segundos;
        public long Nanosegundos;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr hilo, out long creacion, out long salida, out long nucleo, out long usuario);

    [DllImport("libc", SetLastError = true)]
    private static extern int clock_gettime(int reloj, out Timespec tiempo);
}
