namespace CaeManager.Domain.RelacionesEmpresariales;

/// <summary>
/// Contrapartes VIGENTES de una proveedora, clasificadas por el tipo real de
/// la fila de <c>Empresas</c> a la que apuntan — porque
/// <see cref="RelacionEmpresarial.ClienteId"/> no siempre es un Cliente: en
/// la shape Subcontrata→Empresa contiene una Empresa propia.
///
/// <para>
/// <b><see cref="OpacaIds"/> es la pieza de seguridad, no un cajón de
/// sastre</b>: toda contraparte que la consulta de clasificación NO devuelva
/// (soft-deleted — el filtro global de <c>Empresas</c> la oculta — o
/// cualquier fila que no encaje en ninguna categoría) cae ahí, y una opaca
/// <b>jamás puede ser origen de una baja</b>. Es lo que impide que un diff
/// de edición cierre la relación con una contraparte que el usuario ni
/// siquiera pudo ver en pantalla — el fallo de pérdida de datos que la
/// revisión adversarial de F4.2b encontró y que motivó este diseño. El
/// invariante: las bajas se calculan sobre "lo que el usuario pudo
/// desmarcar", nunca sobre "lo que existe".
/// </para>
/// </summary>
public sealed record ContrapartesVigentes(
    IReadOnlyList<Guid> ClienteIds,
    IReadOnlyList<Guid> EmpresaPropiaIds,
    IReadOnlyList<Guid> OpacaIds)
{
    public static readonly ContrapartesVigentes Vacias = new([], [], []);
}
