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
/// <b>Por qué hoy solo <c>SoporteLectura</c>.</b> No es una elección de diseño:
/// es el conjunto actual verificable. La auto-concesión —único camino de
/// creación que existe— solo emite esa capacidad, así que ninguna otra puede
/// materializarse en una fila. Poner aquí más sería afirmar algo que el sistema
/// no sabe honrar todavía.
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
    private static readonly HashSet<CapacidadPrivilegio> Admitidas = [CapacidadPrivilegio.SoporteLectura];

    public static bool Admite(CapacidadPrivilegio capacidad) => Admitidas.Contains(capacidad);
}
