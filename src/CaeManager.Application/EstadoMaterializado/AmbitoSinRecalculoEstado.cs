namespace CaeManager.Application.EstadoMaterializado;

/// <summary>
/// Ámbito explícito «sin recálculo» del estado materializado, para siembras, retirada de un Tenant de demostración y
/// operaciones masivas (diseño § 4.3, revisión M9). Mientras está abierto, el detector de cambios no recalcula nada en
/// línea: por cada Tenant cuyo dato de entrada se guarda, encola una sola entrada «Tenant entero» en
/// <c>RecalculosPendientes</c> (o nada, con <paramref name="encolarAlGuardar"/> a <c>false</c>, para la retirada del
/// Tenant, que borra también la cola).
///
/// <para>
/// Va por flujo asíncrono (<see cref="AsyncLocal{T}"/>), como <see cref="Common.AmbitoTenantExplicito"/>: lo que se lanza
/// dentro lo hereda y lo de fuera no lo ve. Se anida: el más interior manda hasta que se cierra.
/// </para>
/// </summary>
public static class AmbitoSinRecalculoEstado
{
    /// <summary>Motivo de la siembra al arrancar la aplicación (texto de diagnóstico de la cola, no de interfaz).</summary>
    public const string MotivoSiembraDeArranque = "siembra de arranque";

    /// <summary>Motivo de la retirada del Tenant de demostración (texto de diagnóstico, no de interfaz).</summary>
    public const string MotivoRetiradaTenantDemo = "retirada del Tenant de demostración";

    private static readonly AsyncLocal<Ambito?> _actual = new();

    /// <summary>Si hay un ámbito abierto en este flujo.</summary>
    public static bool Activo => _actual.Value is not null;

    /// <summary>Si el ámbito abierto encola el recálculo del Tenant entero; <c>false</c> si no hay ámbito.</summary>
    public static bool EncolaAlGuardar => _actual.Value?.Encolar ?? false;

    /// <summary>Motivo que el detector escribe en la entrada de la cola.</summary>
    public static string? Motivo => _actual.Value?.Motivo;

    /// <param name="motivo">Qué operación lo abre (siembra, importación, retirada), para diagnóstico en la cola.</param>
    /// <param name="encolarAlGuardar">
    /// <c>true</c> (por defecto): cada guardado que toque una entrada del cálculo encola el Tenant entero. <c>false</c>: no se
    /// encola nada (el Tenant se retira).
    /// </param>
    public static IDisposable Abrir(string motivo, bool encolarAlGuardar = true)
    {
        var anterior = _actual.Value;
        _actual.Value = new Ambito(motivo, encolarAlGuardar);
        return new Cierre(anterior);
    }

    private sealed record Ambito(string Motivo, bool Encolar);

    private sealed class Cierre(Ambito? anterior) : IDisposable
    {
        private bool _cerrado;

        public void Dispose()
        {
            if (_cerrado)
                return;
            _cerrado = true;
            _actual.Value = anterior;
        }
    }
}
