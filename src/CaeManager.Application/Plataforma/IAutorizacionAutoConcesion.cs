using CaeManager.Domain.Plataforma;

namespace CaeManager.Application.Plataforma;

/// <summary>
/// ¿Puede este usuario auto-concederse <b>esta</b> capacidad?
///
/// <para>
/// La pregunta lleva la capacidad dentro a propósito. Una interfaz que dijera
/// "¿puede conceder?" sería un emisor universal: quien la superase podría
/// acuñarse cualquier cosa que el enum llegue a tener. Aquí cada capacidad tiene
/// su propia autoridad, enumerada:
/// </para>
/// <code>
/// AdminPlataforma   ←  identidad raíz designada ∧ bootstrap no consumido
/// SoporteLectura    ←  AdminPlataforma vigente
/// cualquier otra    ←  no
/// </code>
///
/// <para>
/// <b>Auto-concederse no es conceder.</b> El comando no tiene parámetro de
/// beneficiario y no lo tendrá: quien ejecuta solo puede darse cosas a sí mismo.
/// Por eso que un <c>AdminPlataforma</c> pueda darse <c>SoporteLectura</c> no
/// adelanta la segregación de funciones que ADR-011 § 4bis.7.7 difiere — esa
/// preocupación es sobre crear autoridad <b>para un tercero</b>, y ese camino
/// sigue sin existir.
/// </para>
///
/// <para>
/// <b>Y tener una no equivale a tener la otra.</b> <c>AdminPlataforma</c> manda
/// en la plataforma; <c>SoporteLectura</c> abre sesiones de lectura sobre un
/// tenant. Que la primera permita darse la segunda es una regla de emisión, no
/// una equivalencia de capacidades.
/// </para>
/// </summary>
public interface IAutorizacionAutoConcesion
{
    Task<bool> PuedeAutoConcederseAsync(
        Guid usuarioId, CapacidadPrivilegio capacidad, CancellationToken cancellationToken = default);
}
