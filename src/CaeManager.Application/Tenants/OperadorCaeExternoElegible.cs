using System.Linq.Expressions;
using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Tenants;

/// <summary>
/// ¿Puede este Tenant recibir, como Operador CAE externo, la operación de un
/// Tenant propietario que ya existe? Único punto de verdad para el incremento 1b
/// (el Administrador del Tenant propietario autoriza a un Operador CAE externo):
/// lo comparten el comando que crea el vínculo y la consulta que resuelve el
/// candidato que ve la pantalla, para que la interfaz no ofrezca nada que el
/// comando rechazaría ni al revés.
///
/// <para>
/// El criterio es el mismo que ya aplica el alta del incremento 1
/// (<c>CrearTenantPropietarioDeOperadorCaeExternoCommand</c>): la capacidad explícita
/// <see cref="Tenant.PuedeActuarComoOperadorCaeExterno"/> (P11) y nunca el Tenant de
/// plataforma, porque TALVEG no es Operador CAE por defecto (ADR-011 § 1). Ya no
/// se usa el criterio interino por perfil de vocabulario
/// (<c>PerfilVocabulario == Consultora &amp;&amp; !EsPlataforma</c>), que P11 retiró de
/// los dos consumidores existentes al fusionar <c>origin/main</c>: repetirlo aquí
/// reabriría la divergencia que P11 cerró.
/// </para>
/// </summary>
public static class OperadorCaeExternoElegible
{
    public static readonly Expression<Func<Tenant, bool>> Predicado =
        t => t.PuedeActuarComoOperadorCaeExterno && !t.EsPlataforma;
}
