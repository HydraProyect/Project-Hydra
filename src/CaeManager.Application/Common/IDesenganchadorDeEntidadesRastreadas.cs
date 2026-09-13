namespace CaeManager.Application.Common;

/// <summary>
/// Deja de rastrear una entidad en el <c>DbContext</c> scoped del circuito o
/// la petición actual.
///
/// <para>
/// Existe para el único caso en que un componente necesita forzar que la
/// <b>siguiente</b> lectura de la misma fila repita el viaje a la base en vez
/// de devolver, por el mapa de identidad de EF, la misma instancia que ya
/// tiene cargada — típicamente para recuperarse de un
/// <c>DbUpdateConcurrencyException</c> que Identity ya atrapó (ver
/// <c>UserStore.UpdateAsync</c>, que lo convierte en un <c>IdentityResult</c>
/// fallido en vez de dejarlo propagar), sin acceso al
/// <c>DbContext</c> concreto: eso rompería <c>Web_no_referencia_CaeManagerDbContext_fuera_del_composition_root</c>
/// / <c>Web_no_referencia_el_namespace_Persistence_de_Infrastructure</c>
/// (<c>CaeManager.Architecture.Tests.FronterasDeCapaTests</c>) — Web resuelve
/// persistencia por interfaces de Application, nunca por el tipo concreto.
/// </para>
/// </summary>
public interface IDesenganchadorDeEntidadesRastreadas
{
    void Desenganchar<TEntidad>(TEntidad entidad) where TEntidad : class;
}
