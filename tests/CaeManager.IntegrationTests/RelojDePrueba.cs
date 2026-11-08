using CaeManager.Domain.Common;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Fija el día de negocio (<see cref="DiaDeNegocio.Hoy()"/>) de un test de integración en un instante, para que
/// la siembra, las superficies que se miden y las aserciones lean todas el mismo «hoy».
///
/// <para>
/// Sin esto, cada lectura de <c>DiaDeNegocio.Hoy()</c> toma el reloj real, y un test que siembra relativo a un
/// «hoy» y mide minutos después ve dos días distintos si la ejecución cruza la medianoche de Europe/Madrid
/// (issue #2073: la cola de fusión de la PR #2071 cruzó 00:00 hora peninsular y rompió
/// <c>Dos_siembras_independientes_reparten_igual_el_semaforo</c> y
/// <c>La_misma_vigencia_da_el_mismo_estado_en_todas_las_superficies</c>).
/// </para>
///
/// <para>
/// El reloj es del flujo asíncrono (<c>AsyncLocal</c>): se llama dentro del cuerpo de cada test —o de su
/// <c>InitializeAsync</c>, que no propaga el valor al test— y se libera con <c>using</c>. El instante lo captura
/// el test UNA vez (<see cref="DateTimeOffset.UtcNow"/> al empezar) y se reutiliza para todo lo que dependa de él.
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
