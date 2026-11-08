using CaeManager.Domain.Common;

namespace CaeManager.Web.Tests;

/// <summary>
/// Fija el día de negocio (<see cref="DiaDeNegocio.Hoy()"/>) de un test de Web en un instante, para que lo que el test
/// siembra relativo a «hoy» y lo que el componente o el filtro leen al medir sean el mismo día aunque la ejecución cruce
/// la medianoche de Europe/Madrid (issue #2073). Equivalente al <c>RelojDePrueba</c> de IntegrationTests, que este
/// proyecto no referencia.
///
/// <para>
/// El reloj es del flujo asíncrono (<c>AsyncLocal</c>): se llama dentro del cuerpo de cada test y se libera con
/// <c>using</c>. El instante lo captura el test UNA vez (<see cref="DateTimeOffset.UtcNow"/> al empezar). Solo vale para
/// código que corre en el flujo del test (bUnit, filtros llamados directamente); un servidor Kestrel real no lo ve.
/// </para>
/// </summary>
internal static class RelojDePrueba
{
    /// <summary>Fija <see cref="DiaDeNegocio.Hoy()"/> en <paramref name="instante"/> hasta liberar el valor devuelto.</summary>
    public static IDisposable FijarEn(DateTimeOffset instante) =>
        DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(instante));

    private sealed class RelojFijo(DateTimeOffset instante) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instante;
    }
}