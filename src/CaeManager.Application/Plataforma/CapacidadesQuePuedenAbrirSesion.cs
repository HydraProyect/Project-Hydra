using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Plataforma;

/// <summary>
/// Qué capacidades concedidas autorizan, además, a <b>abrir</b> la ceremonia.
///
/// <para>
/// La distinción que esta lista materializa es la de ADR-011 § 8.4, y sin ella
/// la ceremonia se derrumba:
/// </para>
/// <code>
/// capacidad DE la sesión        ≠   capacidad para ABRIRLA
/// qué puedes hacer dentro           que puedas iniciarla siquiera
/// </code>
/// <para>
/// Dicho en la trampa concreta: si "tener una concesión vigente" bastara para
/// abrir, cualquier capacidad futura se convertiría en llave de la ceremonia el
/// día que alguien la añadiera al enum, sin que nadie tomara esa decisión.
/// </para>
///
/// <para>
/// <b>Por qué hoy <c>SoporteLectura</c>, <c>Aprovisionamiento</c> y
/// <c>RestablecimientoSegundoFactor</c>.</b> No es una elección de diseño
/// ampliada a la ligera: son los caminos de creación que existen. La
/// auto-concesión solo emite <c>SoporteLectura</c>; <c>ConcederPrivilegioCommand</c>
/// emite <c>Aprovisionamiento</c> (PD-A3) y <c>RestablecimientoSegundoFactor</c>
/// (ADR-011 § 8.7, punto 3, restablecimiento del Administrador único), y
/// <c>ConcesionesSoloPorActoExplicitoTests</c> mantiene esa lista cerrada.
/// Ninguna otra capacidad puede materializarse en una fila hoy, así que poner
/// más aquí seguiría afirmando algo que el sistema no sabe honrar.
/// </para>
///
/// <para>
/// <b>Punto de extensión, no ampliación.</b> Que <c>AdminPlataforma</c> pueda o
/// no abrir sesiones —y de qué capacidad— se deriva de la matriz de capacidades,
/// en su propio incremento. Al ser lista explícita, entrar aquí exige un cambio
/// deliberado con su justificación escrita: nunca llega como efecto colateral de
/// que una capacidad exista.
/// </para>
/// </summary>
public static class CapacidadesQuePuedenAbrirSesion
{
    private static readonly HashSet<CapacidadPrivilegio> Admitidas =
        [
            CapacidadPrivilegio.SoporteLectura,
            CapacidadPrivilegio.Aprovisionamiento,
            CapacidadPrivilegio.RestablecimientoSegundoFactor,
        ];

    public static bool Admite(CapacidadPrivilegio capacidad) => Admitidas.Contains(capacidad);
}
