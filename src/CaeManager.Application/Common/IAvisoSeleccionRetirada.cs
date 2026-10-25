namespace CaeManager.Application.Common;

/// <summary>
/// Avisa de que la selección de Tenant beneficiario de este ámbito (un circuito de Blazor Server) dejó de valer: ventana de
/// soporte terminada, delegación revocada u operación cerrada. A partir de ahí <see cref="ITenantActual"/> devuelve otro
/// Tenant (el de origen) en el mismo circuito.
///
/// <para>
/// Lo implementa la selección de la capa Web, y el <see cref="ITenantActual"/> de esa capa lo reenvía: quien lo necesita
/// lo comprueba con <c>is</c> sobre el <see cref="ITenantActual"/> inyectado, para no obligar a cada doble de pruebas de
/// esas interfaces a implementarlo. Sirve para que lo que el circuito ya suscribió con el Tenant
/// anterior (avisos del estado materializado, mensajes en tiempo real) se dé de baja sin esperar al cierre del circuito.
/// No decide nada ni autoriza nada.
/// </para>
///
/// <para>
/// El evento se dispara en el hilo de quien retira la selección (el temporizador de revalidación del circuito o la
/// petición que la invalida), no en el despachador del componente, y <b>después</b> de que la selección ya no esté:
/// quien lo escuche y lea <see cref="ITenantActual.TenantId"/> ya ve el Tenant nuevo. Un oyente que lance no impide que
/// los demás reciban el aviso.
/// </para>
/// </summary>
public interface IAvisoSeleccionRetirada
{
    /// <summary>La selección que estaba viva se retiró; el argumento es el Tenant beneficiario que tenía seleccionado.</summary>
    event Action<Guid>? TenantSeleccionadoRetirado;
}
